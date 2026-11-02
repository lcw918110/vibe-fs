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

    let private runFetch
        (owner: CasebookSettlementOwner)
        (language: ProviderLanguage)
        (workspaceRoot: string)
        (store: IEventStore)
        (shelfmark: string)
        : System.Threading.Tasks.Task<string> =
        task {
            let notifyFailure failure = owner.Observe failure |> ignore
            let! result = CasebookServiceBinding.fetch workspaceRoot store shelfmark notifyFailure

            match unbox<string> result?kind with
            | "unavailable" -> return unavailable language
            | "missing" -> return noCase language
            | "fresh" -> return fresh language (unbox result?answer)
            | "refreshed" -> return refreshed language (unbox result?answer)
            | "stale" -> return stale language (unbox result?answer)
            | kind -> return invalidOp $"unknown casebook fetch kind: {kind}"
        }

    let private getOrCreateFlightWork
        (owner: CasebookSettlementOwner)
        (language: ProviderLanguage)
        (workspaceRoot: string)
        (store: IEventStore)
        (shelfmark: string)
        : System.Threading.Tasks.Task<string> =
        let factory = fun () -> box (runFetch owner language workspaceRoot store shelfmark)

        let promise =
            fetchFlightRegistry?getOrCreate (workspaceRoot, shelfmark, box store, factory)

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
