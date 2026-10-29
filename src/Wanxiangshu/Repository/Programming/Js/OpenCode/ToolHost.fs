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

    [<Emit("($0 != null && typeof $0 === 'object' && !Array.isArray($0) && Object.keys($0).every(k => ['name', 'program', 'intent', 'scope'].includes(k)) && ['name','program','intent'].every(k => Object.prototype.hasOwnProperty.call($0, k)))")>]
    let private hasOnlyAllowedArguments (_source: obj) : bool = jsNative


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

        let inputOf (args: HostToolArguments) : obj option =
            if not (hasOnlyAllowedArguments args.Raw) then None
            else
            match args.OptionalText "name", args.OptionalText "program", args.OptionalText "intent" with
            | Some name, Some program, Some intent ->
                Some(createObj [ "name" ==> name; "program" ==> program; "intent" ==> intent ])
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

        { Name = surface.ToolName
          Description = surface.Description
          Arguments =
            [ "name", ToolHostCodec.stringSchemaDescribed (JsToolPolicy.argNameProse ()) factory
              "program", ToolHostCodec.stringSchemaDescribed (JsToolPolicy.argProgramProse ()) factory
              "intent", ToolHostCodec.stringSchemaDescribed (JsToolPolicy.argIntentProse ()) factory ]
          Execute =
            fun args ctx ->
                match inputOf args with
                | Some input -> runProgramCall ctx input null
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
