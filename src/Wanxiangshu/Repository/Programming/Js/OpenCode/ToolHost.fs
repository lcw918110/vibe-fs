namespace Wanxiangshu.Repository.Programming.Js.OpenCode

open System.Threading.Tasks
open Fable.Core
open Fable.Core.JsInterop
open Wanxiangshu.Foundation.Identity
open Wanxiangshu.Host
open Wanxiangshu.Host.Ports
open Wanxiangshu.OpenCode
open Wanxiangshu.Repository.Programming.Js


/// JS-073/JS-074: a generated js-* tool spec — the dynamic counterpart of the
/// static baseSpecs. Built from a generated surface (JS-002); execution goes
/// through JsToolWorkflow (sandbox → staging → preflight → durable facts →
/// commit) and the result renders through JsToolsResult (JS-016).
module JsToolSpec =

    [<Import("scopeProgress", "wanxiangli/scope-progress")>]
    let private scopeProgress: obj = jsNative

    [<Emit("($0 != null && typeof $0 === 'object' && !Array.isArray($0) && Object.keys($0).length === 4 && Object.keys($0).every(k => ['name', 'program', 'intent', 'remainingScopeTurnsEstimate'].includes(k)))")>]
    let private hasOnlyAllowedArguments (_source: obj) : bool = jsNative

    let private progressRefusal (receipt: obj) : string option =
        if isNull receipt?ok || not (unbox<bool> receipt?ok) then
            Some("Scope progress refused: " + string receipt?reason)
        else None

    /// Build a ToolSpec for one generated surface.
    ///
    /// `workspaceRoot` is where the tool executes; `persistence` enables the
    /// durable transaction facts (JS-012) when the caller has an EventStore.
    /// `modelSourceProvider` supplies the model program (the tool arguments);
    /// the first version reads it from the Host arguments payload.
    let private createCore
        (readCheck: (HostToolContext -> Task<bool>) option)
        (writeCheck: (HostToolContext -> bool) option)
        (factory: HostToolFactory)
        (surface: JsSurface)
        (workspaceRoot: string)
        (persistence: IJsTransactionPersistence option)
        (lifecyclePersistence: IJsLifecyclePersistence option)
        (fileAccessObservation: (HostToolContext -> JsExplicitFileRead list -> string list -> Task<unit>) option)
        (rootOf: string -> string)
        (settlementOf: HostToolContext -> Task<DeliverySettlement option>)
        (epochOf: HostToolContext -> string)
        : ToolSpec =
        let readCheck = JsToolPolicy.defaultReadCheck surface.RoleName readCheck

        let inputOf (args: HostToolArguments) : (obj * int) option =
            if not (hasOnlyAllowedArguments args.Raw) then None
            else
            match args.OptionalText "name", args.OptionalText "program", args.OptionalText "intent", args.OptionalNonNegativeInteger "remainingScopeTurnsEstimate" with
            | Some name, Some program, Some intent, Ok(Some estimate) ->
                Some(createObj [ "name" ==> name; "program" ==> program; "intent" ==> intent ], estimate)
            | _ -> None

        let callFenceOf (ctx: HostToolContext) =
            match ctx.ProviderRunId, ctx.ToolCallId with
            | Some providerRun, Some toolCallId -> Some(providerRun, toolCallId)
            | _ -> None

        // A delivery witness belongs to a fenced call: without the
        // call fence there is nothing that can own the witness.
        // A fence-less call therefore never carries a settlement.
        // RT-02: attribution resolution is deferred to delivery time.
        let settlementFor (ctx: HostToolContext) fence : DeliverySettlement option =
            match fence with
            | None -> None
            | Some _ -> Some(DeliverySettlement.DeferredDelivery(fun () -> settlementOf ctx))

        let recordProgress (ctx: HostToolContext) (estimate: int) =
            match callFenceOf ctx with
            | None -> None
            | Some (providerRun, toolCallId) ->
                let receipt: obj = scopeProgress?record(ctx.SessionId, epochOf ctx, ProviderRunIdentity.value providerRun, ToolCallId.value toolCallId, estimate)
                progressRefusal receipt

        let runProgramCall (ctx: HostToolContext) (input: obj) (declaration: obj) =
            task {
                // The session's own directory wins over the plugin workspace:
                // a road's deliverable belongs in the worktree that session was
                // created in, not in the target's checkout.
                let (Ports.WorkspacePath rootPath) = Ports.WorkspacePath(rootOf ctx.SessionId)
                let root = rootPath
                let fence = callFenceOf ctx

                let callIdentity =
                    fence
                    |> Option.map (fun (providerRun, toolCallId) ->
                        ProviderRunIdentity.value providerRun, ToolCallId.value toolCallId)

                let settlement = settlementFor ctx fence

                let reviewGates =
                    if readCheck.IsNone && writeCheck.IsNone then
                        None
                    else
                        Some
                            { ReadGate = readCheck |> Option.map (fun check -> fun () -> check ctx)
                              WriteGate = writeCheck |> Option.map (fun check -> fun () -> check ctx) }

                // Without a settlement this is exactly executeWithReviewCheck.
                let! outcome =
                    JsToolWorkflow.executeWithReviewCheckAndDelivery
                        reviewGates
                        settlement
                        ctx.SessionId
                        fence
                        surface.Capabilities
                        root
                        surface.BaseClassSource
                        input
                        10000
                        0L
                        (1 <<< 20)
                        persistence
                        lifecyclePersistence
                        (fileAccessObservation |> Option.map (fun observe -> observe ctx))

                return JsToolPolicy.renderCommitted ctx.SessionId callIdentity declaration outcome
            }

        let launchAccepted ctx input estimate =
            match recordProgress ctx estimate with
            | Some reason -> Task.FromResult reason
            | None -> runProgramCall ctx input null

        { Name = surface.ToolName
          Description = surface.Description
          Arguments =
            [ "name", ToolHostCodec.stringSchemaDescribed (JsToolPolicy.argNameProse ()) factory
              "program", ToolHostCodec.stringSchemaDescribed (JsToolPolicy.argProgramProse ()) factory
              "intent", ToolHostCodec.stringSchemaDescribed (JsToolPolicy.argIntentProse ()) factory
              "remainingScopeTurnsEstimate", ToolHostCodec.nonNegativeIntegerSchemaDescribed
                  (JsToolPolicy.argEstimateProse ()) factory ]
          Execute =
            fun args ctx ->
                match inputOf args with
                | Some (input, estimate) -> launchAccepted ctx input estimate
                | None -> Task.FromResult(JsToolPolicy.missingProgramResult ()) }

    let create
        factory
        surface
        workspaceRoot
        persistence
        lifecyclePersistence
        fileAccessObservation
        rootOf
        settlementOf
        epochOf
        =
        createCore
            None
            None
            factory
            surface
            workspaceRoot
            persistence
            lifecyclePersistence
            fileAccessObservation
            rootOf
            settlementOf
            epochOf

    let createWithReviewCheck
        check
        factory
        surface
        workspaceRoot
        persistence
        lifecyclePersistence
        fileAccessObservation
        rootOf
        settlementOf
        epochOf
        =
        createCore
            (Some check)
            None
            factory
            surface
            workspaceRoot
            persistence
            lifecyclePersistence
            fileAccessObservation
            rootOf
            settlementOf
            epochOf

    let createWithPlanWriteCheck
        check
        factory
        surface
        workspaceRoot
        persistence
        lifecyclePersistence
        fileAccessObservation
        rootOf
        settlementOf
        epochOf
        =
        createCore
            None
            (Some check)
            factory
            surface
            workspaceRoot
            persistence
            lifecyclePersistence
            fileAccessObservation
            rootOf
            settlementOf
            epochOf
