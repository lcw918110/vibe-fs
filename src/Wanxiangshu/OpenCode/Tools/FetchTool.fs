namespace Wanxiangshu.OpenCode

open System
open Fable.Core
open Fable.Core.JsInterop
open Wanxiangshu.Host
open Wanxiangshu.Foundation
open Wanxiangshu.Participant.Provider
open Wanxiangshu.Persistence.EventStore
open Wanxiangshu.Repository.Knowledge.Casebook

/// Conditional Casebook read. Provider identity is a public shelfmark; durable
/// session identity, freshness state and maintenance machinery remain internal.
module FetchTool =

    [<RequireQualifiedAccess>]
    module Path =
        [<Literal>]
        let Description = "tool/fetch/description"

        [<Literal>]
        let Fresh = "tool/fetch/fresh"

        [<Literal>]
        let Refreshed = "tool/fetch/refreshed"

        [<Literal>]
        let Stale = "tool/fetch/stale"

        [<Literal>]
        let NoCase = "tool/fetch/no-case"

        [<Literal>]
        let Unavailable = "tool/fetch/unavailable"

        [<Literal>]
        let ShelfmarkRequired = "tool/fetch/shelfmark-required"

    [<Import("fetchFlightRegistry", "wanxiangli/casebook")>]
    let private fetchFlightRegistry: obj = jsNative

    let private lang (ctx: HostToolContext) =
        ProviderLanguageBinding.forSessionText ctx.SessionId

    let private prose language path =
        ProviderProse.render language path Map.empty

    let private answerResult (consequence: string) (answer: string) =
        ToolResultText.tomlObjectWithInstructions [ consequence ] [ "answer", ToolResultText.TString answer ]

    let private fresh language answer =
        answerResult (prose language Path.Fresh) answer

    let private refreshed language answer =
        answerResult (prose language Path.Refreshed) answer

    let private stale language answer =
        answerResult (prose language Path.Stale) answer

    let private noCase language =
        ToolResultText.tomlObjectWithInstructions [ prose language Path.NoCase ] []

    let private unavailable language =
        ToolResultText.tomlObjectWithInstructions [ prose language Path.Unavailable ] []

    let private observeMutationFailure (owner: CasebookSettlementOwner) (error: CasebookMutationError) =
        match error with
        | CasebookMutationError.AppendFailure failure -> owner.Observe failure |> ignore
        | _ -> ()

    let private touchAccess owner workspaceRoot store identity =
        task {
            match! CasebookLifecycle.touchAccess workspaceRoot store identity with
            | Ok() -> ()
            | Error error -> observeMutationFailure owner error
        }

    /// Serve the maintained body of a case reported as changed, or stay stale.
    let private serveMaintained owner language workspaceRoot store identity (cachedAnswer: string) =
        task {
            let! latest = CasebookWorkflow.fetchCase store 256 identity

            match latest with
            | Ok(Some updated) ->
                do! touchAccess owner workspaceRoot store identity
                return refreshed language updated.A
            | _ -> return stale language cachedAnswer
        }

    /// Maintain the case from the provided diff, then serve the maintained body.
    let private refreshFromDiff owner language workspaceRoot store identity (cachedAnswer: string) =
        task {
            let! changed = CasebookBookkeeper.refreshStale store workspaceRoot identity

            match changed with
            | Ok true -> return! serveMaintained owner language workspaceRoot store identity cachedAnswer
            | Ok false ->
                do! touchAccess owner workspaceRoot store identity
                return fresh language cachedAnswer
            | Error error ->
                observeMutationFailure owner error
                return stale language cachedAnswer
        }

    let private handleResolvedCase owner language workspaceRoot store (case: Case) =
        refreshFromDiff owner language workspaceRoot store case.Identity case.A

    let private runFetch
        (owner: CasebookSettlementOwner)
        (language: ProviderLanguage)
        (workspaceRoot: string)
        (store: IEventStore)
        (shelfmark: string)
        : System.Threading.Tasks.Task<string> =
        task {
            match CasebookProjection.resolve store 256 shelfmark with
            | Error _ -> return unavailable language
            | Ok None -> return noCase language
            | Ok(Some case) -> return! handleResolvedCase owner language workspaceRoot store case
        }

    let private getOrCreateFlightWork
        (owner: CasebookSettlementOwner)
        (language: ProviderLanguage)
        (workspaceRoot: string)
        (store: IEventStore)
        (shelfmark: string)
        : System.Threading.Tasks.Task<string> =
        let factory = fun () -> box (runFetch owner language workspaceRoot store shelfmark)
        let promise = fetchFlightRegistry?getOrCreate (workspaceRoot, shelfmark, box store, factory)
        unbox<System.Threading.Tasks.Task<string>> promise

    let spec
        (factory: HostToolFactory)
        (workspaceRoot: string)
        (store: IEventStore)
        (owner: CasebookSettlementOwner)
        : ToolSpec =
        if isNull (box owner) then
            nullArg "owner"

        { Name = "fetch"
          Description = prose (ProviderLanguageBinding.readGlobalPreference ()) Path.Description
          Arguments = [ "shelfmark", ToolHostCodec.stringSchema factory ]
          Execute =
            fun args ctx ->
                task {
                    let language = lang ctx
                    let shelfmark = args.Text "shelfmark"

                    if not (CasebookFeature.isEnabled workspaceRoot) then
                        return unavailable language
                    elif String.IsNullOrWhiteSpace shelfmark then
                        return ToolResultText.tomlObjectWithInstructions [ prose language Path.ShelfmarkRequired ] []
                    else
                        return! getOrCreateFlightWork owner language workspaceRoot store shelfmark
                } }
