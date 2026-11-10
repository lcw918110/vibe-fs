namespace Wanxiangshu.Repository.Knowledge.Casebook

open System
open System.Threading.Tasks
open Fable.Core
open Fable.Core.JsInterop
open FsToolkit.ErrorHandling
open Wanxiangshu.Foundation
open Wanxiangshu.Foundation.Identity
open Wanxiangshu.Foundation.Outcome
open Wanxiangshu.Host
open Wanxiangshu.Host.Contract
open Wanxiangshu.Interaction.Authority
open Wanxiangshu.Participant.Persona
open Wanxiangshu.Participant.Provider

module CasebookBookkeeperSurface =

    let renderSuccess (languageName: string) (value: obj) =
        BookkeeperPolicy.succeeded (ProviderLanguage.parse languageName) value

    [<Import("bookkeeperStaging", "wanxiangli/casebook")>]
    let private staging: obj = jsNative

    [<Emit("$0 == null")>]
    let private isNullish (value: obj) : bool = jsNative

    let createRefreshPrompt (input: obj) : string =
        let q = if isNullish (input?q) then "" else string input?q
        let a = if isNullish (input?a) then "" else string input?a
        let diff = if isNullish (input?diff) then "" else string input?diff

        let relatedPaths =
            if isNullish (input?relatedPaths) then
                []
            else
                unbox<string array> (input?relatedPaths) |> Array.toList

        let language =
            if isNullish (input?language) then
                ProviderLanguage.English
            else
                ProviderLanguage.parse (string input?language)

        BookkeeperRuntime.createRefreshPromptFor language q a relatedPaths diff

    let private requiredString (fieldName: string) (value: obj) : Result<string, string> =
        let isString: bool = emitJsExpr value "typeof $0 === 'string'"

        if not isString || String.IsNullOrWhiteSpace(unbox<string> value) then
            Error(sprintf "invalid bookkeeper owner descriptor: %s must be a non-empty string" fieldName)
        else
            Ok(unbox<string> value)

    let private ownerProfile (descriptor: obj) =
        if isNullish descriptor then
            Error "invalid bookkeeper owner descriptor: descriptor must be an object"
        else
            result {
                let! sessionId = requiredString "sessionId" descriptor?sessionId
                let! logicalRunId = requiredString "logicalRunId" descriptor?logicalRunId

                let! authorityRootUserMessageId =
                    requiredString "authorityRootUserMessageId" descriptor?authorityRootUserMessageId

                let! agent = requiredString "agent" descriptor?agent

                let! rootSelection =
                    (match ParticipantIdentity.resolveAtRoot agent with
                     | Ok s -> Ok s
                     | Error(ParticipantIdentityError.LegacyParticipantName _) ->
                         ParticipantIdentity.resolveAtRoot "engineer"
                     | Error e -> Error e)
                    |> Result.map PromptAuthority.IdentitySeed.RootSelection
                    |> Result.mapError (sprintf "invalid bookkeeper owner descriptor agent: %A")

                let! profile =
                    PromptAuthority.createAuthorityExecutionProfileFromSeed
                        (SessionId.create sessionId)
                        (LogicalRunId.create logicalRunId)
                        (AuthorityRootUserMessageId.create authorityRootUserMessageId)
                        PromptAuthority.RootAuthorityKind.HumanRoot
                        rootSelection

                return sessionId, profile
            }

    let private ownerLookup (ownerDescriptors: obj) =
        let isArray: bool = emitJsExpr ownerDescriptors "Array.isArray($0)"

        if not isArray then
            Error "invalid bookkeeper owner descriptors: expected an array"
        else
            unbox<obj array> ownerDescriptors
            |> Array.toList
            |> List.traverseResultM ownerProfile
            |> Result.bind (fun owners ->
                let lookup = Map.ofList owners

                if Map.count lookup <> List.length owners then
                    Error "invalid bookkeeper owner descriptors: duplicate sessionId"
                else
                    Ok lookup)

    let setRuntime (port: obj) (ownerDescriptors: obj) : obj =
        match ownerLookup ownerDescriptors with
        | Error error -> box {| ok = false; error = error |}
        | Ok owners ->
            let sessions = unbox<Wanxiangshu.Foundation.HostSession.ISessionHostPort> port

            let kbPort =
                { new ICasebookSessionPort with
                    member _.AbortSession childId = sessions.AbortSession childId

                    member _.SubscribeTerminal(childId, listener) =
                        sessions.SubscribeTerminal(
                            childId,
                            (fun id outcome ->
                                let res =
                                    match outcome with
                                    | Wanxiangshu.Foundation.TerminalSignal.TerminalOutcome.Completed _ -> Ok()
                                    | Wanxiangshu.Foundation.TerminalSignal.TerminalOutcome.Failed stop ->
                                        Error stop.Reason
                                    | Wanxiangshu.Foundation.TerminalSignal.TerminalOutcome.Aborted stop ->
                                        Error stop.Reason

                                listener id res)
                        )

                    member _.SendPrompt(childId, text, agent) =
                        task {
                            let exactTools = Map.ofList [ "*", false; "todowrite-for-bookkeeper", true ]

                            let opts: Wanxiangshu.Foundation.HostSession.SessionPromptOptions =
                                { Model = None
                                  Agent = Some agent
                                  Directory = None
                                  Metadata = None
                                  Delivery = None
                                  Tools = Some exactTools
                                  DetachedListener = None }

                            let! outcome = sessions.SendPrompt(childId, text, opts)

                            match outcome with
                            | Wanxiangshu.Foundation.Outcome.SendOutcome.AdmittedWithReceipt _
                            | Wanxiangshu.Foundation.Outcome.SendOutcome.AdmittedWithPhysicalMessage _ -> return Ok()
                            | Wanxiangshu.Foundation.Outcome.SendOutcome.Retryable r
                            | Wanxiangshu.Foundation.Outcome.SendOutcome.Fatal r
                            | Wanxiangshu.Foundation.Outcome.SendOutcome.AcceptanceUnknown r -> return Error r
                        }

                    member _.CreateSiblingSession(owner, title, agent) =
                        sessions.CreateSiblingSession(
                            owner,
                            None,
                            { Title = Some title
                              Agent = Some agent
                              Directory = None }
                        ) }

            BookkeeperRuntime.setPort kbPort (SessionId.value >> (fun sessionId -> Map.tryFind sessionId owners))

            box {| ok = true |}

    let resetRuntime () : unit = BookkeeperRuntime.resetRuntime ()

    let bindSession (sessionId: string) (txId: string) (ownerSessionId: string) : unit =
        BookkeeperRuntime.bindSession sessionId txId ownerSessionId

    let unbindSession (sessionId: string) : unit =
        BookkeeperRuntime.unbindSession sessionId

    let txIdFor (sessionId: string) : string = BookkeeperRuntime.txIdFor sessionId

    let beginTransaction (txId: string) (question: string) (answer: string) : unit =
        staging?beginTransaction (txId, question, answer) |> ignore

    let abort (txId: string) : unit = staging?abort (txId) |> ignore

    let private stagedToJs (result: Result<string * string, string>) : obj =
        match result with
        | Ok(question, answer) ->
            box
                {| ok = true
                   value = box [| box question; box answer |] |}
        | Error message -> box {| ok = false; error = message |}

    let snapshot (txId: string) : obj =
        unbox<Result<string * string, string>> (staging?snapshot (txId)) |> stagedToJs

    let take (txId: string) : obj =
        unbox<Result<string * string, string>> (staging?take (txId)) |> stagedToJs

    let sessionId (value: string) : obj = box (SessionId.create value)

    let sessionValue (value: obj) : string =
        SessionId.value (unbox<SessionId> value)

    let acceptedSession (value: string) : obj =
        box (Ok(SessionId.create value): Result<SessionId, string>)

    let acceptedPrompt () : obj =
        box (AdmittedWithReceipt(TransportReceipt.create "bookkeeper-accepted"))

    let failedPrompt (reason: string) : obj = box (Retryable reason)

    let aborted () : obj = box (Ok())

    let completed (value: string) : obj =
        // Synthetic leaf terminal: the Bookkeeper leaf runs under the owner
        // descriptor's converged research identity, and this tag only labels
        // the child turn for reconciliation consumers that ignore it.
        box (
            Wanxiangshu.Foundation.TerminalSignal.TerminalOutcome.Completed
                { SessionId = SessionId.create value
                  AuthorityRootUserMessageId = AuthorityRootUserMessageId.create "bookkeeper"
                  ProviderRun = ProviderRunIdentity.create "bookkeeper"
                  Role = Role.Engineer
                  Directory = None
                  TerminalText = "bookkeeper completed"
                  TurnFormalText = "bookkeeper completed" }
        )
