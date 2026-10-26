namespace Wanxiangshu.Repository.Knowledge.Casebook

open System
open System.Collections.Generic
open System.Threading.Tasks
open Fable.Core
open Fable.Core.JsInterop
open Wanxiangshu.Foundation
open Wanxiangshu.Foundation.Identity
open Wanxiangshu.Foundation.Outcome
open Wanxiangshu.Interaction.Authority
open Wanxiangshu.Participant.Persona
open Wanxiangshu.Participant.Provider
open Wanxiangshu.Resources

[<RequireQualifiedAccess>]
type BookkeeperRequest =
    | CaseRefresh
    | CaseFinalize

type ICasebookSessionPort =
    abstract AbortSession: childId: SessionId -> Task<Result<unit, string>>
    abstract SubscribeTerminal: childId: SessionId * (SessionId -> Result<unit, string> -> unit) -> IDisposable
    abstract SendPrompt: childId: SessionId * promptText: string * agent: string -> Task<Result<unit, string>>

    abstract CreateSiblingSession:
        ownerSessionId: SessionId * title: string * agent: string -> Task<Result<SessionId, string>>

/// Physical Bookkeeper leaf: one CreateChildSession per transaction, todowrite-for-bookkeeper
/// against process-local staging, then AbortSession.
module BookkeeperRuntime =

    [<Import("bookkeeperStaging", "wanxiangli/casebook")>]
    let private staging: obj = jsNative

    [<Import("bookkeeperLedger", "wanxiangli/casebook")>]
    let private bookkeeperLedger: obj = jsNative

    type private Runtime =
        { Port: ICasebookSessionPort
          ResolveActiveOwner: SessionId -> PromptAuthority.AuthorityExecutionProfile option }

    /// Pure domain decisions: prompt/evidence shaping and receipt classification.
    module private Decisions =

        let requestInstructions (lang: ProviderLanguage) (path: string) =
            PromptResources.bookkeeperInstructionTextsFor lang
            @ [ ProviderProse.render lang path Map.empty ]

        let evidencePatch (observations: Observation list) : string =
            observations
            |> Observations.normalize
            |> List.map (fun observation ->
                match observation with
                | Observation.FileRead(path, hash) -> "read " + path + " " + hash
                | Observation.GlobResult(pattern, paths) ->
                    "glob " + pattern + " " + (paths |> List.sort |> String.concat ",")
                | Observation.GrepResult(pattern, matches) ->
                    let flat =
                        matches
                        |> List.map (fun (path, index, text) -> path + "@" + string index + ":" + text)
                        |> List.sort
                        |> String.concat "|"

                    "grep " + pattern + " " + flat)
            |> String.concat "\n"

        let createRefreshPrompt
            (lang: ProviderLanguage)
            (q: string)
            (a: string)
            (relatedPaths: string list)
            (diff: string)
            : string =
            LlmFacing.instructions (requestInstructions lang "casebook/refresh")
            |> LlmFacing.withData
                [ LlmFacing.Data.table "request" [ LlmFacing.Data.stringMember "kind" "CaseRefresh" ]
                  LlmFacing.Data.table "question" [ LlmFacing.Data.stringMember "content" q ]
                  LlmFacing.Data.table "answer" [ LlmFacing.Data.stringMember "content" a ]
                  LlmFacing.Data.table
                      "related_paths"
                      [ LlmFacing.Data.stringMember "paths" (relatedPaths |> String.concat "\n") ]
                  LlmFacing.Data.table "diff" [ LlmFacing.Data.stringMember "content" diff ] ]
            |> LlmFacing.render

        let private transcriptTable (textOpt: string option) : LlmFacing.DataBlock list =
            match textOpt |> Option.filter (String.IsNullOrWhiteSpace >> not) with
            | Some text -> [ LlmFacing.Data.table "transcript" [ LlmFacing.Data.stringMember "content" text ] ]
            | None -> []

        let envelope
            (kind: BookkeeperRequest)
            (ownerSessionId: string)
            (q: string)
            (a: string)
            (observations: Observation list)
            (extraTranscript: string option)
            : string =
            match kind with
            | BookkeeperRequest.CaseRefresh ->
                let diffText = extraTranscript |> Option.defaultValue ""

                let related =
                    observations
                    |> List.choose (function
                        | Observation.FileRead(p, _) -> Some p
                        | _ -> None)

                createRefreshPrompt (ProviderProse.languageOf (SessionId.create ownerSessionId)) q a related diffText
            | BookkeeperRequest.CaseFinalize ->
                let kindLabel = "CaseFinalize"

                let transcriptBlock = transcriptTable extraTranscript

                LlmFacing.instructions (
                    requestInstructions (ProviderProse.languageOf (SessionId.create ownerSessionId)) "casebook/finalize"
                )
                |> LlmFacing.withData (
                    [ LlmFacing.Data.table "request" [ LlmFacing.Data.stringMember "kind" kindLabel ]
                      LlmFacing.Data.table "case" [ LlmFacing.Data.stringMember "session_id" ownerSessionId ]
                      LlmFacing.Data.table "question" [ LlmFacing.Data.stringMember "content" q ]
                      LlmFacing.Data.table "answer" [ LlmFacing.Data.stringMember "content" a ]
                      LlmFacing.Data.table
                          "source_observations"
                          [ LlmFacing.Data.stringMember "records" (evidencePatch observations) ] ]
                    @ transcriptBlock
                )
                |> LlmFacing.render

        let canonicalAgent = ManagedAgentCatalog.bookkeeperName

    let createRefreshPrompt (q: string) (a: string) (relatedPaths: string list) (diff: string) : string =
        Decisions.createRefreshPrompt ProviderLanguage.English q a relatedPaths diff

    let createRefreshPromptFor
        (lang: ProviderLanguage)
        (q: string)
        (a: string)
        (relatedPaths: string list)
        (diff: string)
        : string =
        Decisions.createRefreshPrompt lang q a relatedPaths diff

    let private runtimeGate = obj ()
    // DSL-MUTABLE: resource — host-side session port and active owner resolution.
    let mutable private runtime: Runtime option = None

    /// Host-effect coordination.
    module private Coordination =

        let retire (sessions: ICasebookSessionPort) (childId: SessionId) : Task<unit> =
            task {
                try
                    let! _ = sessions.AbortSession childId
                    ()
                with _ ->
                    ()

                bookkeeperLedger?unbind (SessionId.value childId)
            }

        let awaitCompletion
            (sessions: ICasebookSessionPort)
            (txId: string)
            (childId: SessionId)
            (completion: TaskCompletionSource<Result<unit, string>>)
            (disposeSub: unit -> unit)
            : Task<Result<string * string, string>> =
            task {
                let! waited = completion.Task
                disposeSub ()

                match waited with
                | Error err ->
                    staging?abort (txId) |> ignore
                    do! retire sessions childId
                    return Error err
                // LC-029: the staged transaction starts with the initial
                // Q/A, so a terminal that never wrote through it has not
                // delivered. Keep waiting instead of passing the original
                // values off as this run's commitment.
                | Ok() when unbox<bool> (staging?touched (txId)) ->
                    let taken = unbox<Result<string * string, string>> (staging?take (txId))
                    do! retire sessions childId
                    return taken
                | Ok() ->
                    staging?abort (txId) |> ignore
                    do! retire sessions childId
                    return Error "bookkeeper program did not write the staged case"
            }

        let sendBookkeeperPrompt
            (sessions: ICasebookSessionPort)
            (txId: string)
            (childId: SessionId)
            (completion: TaskCompletionSource<Result<unit, string>>)
            (disposeSub: unit -> unit)
            (promptText: string)
            (agent: string)
            : Task<Result<string * string, string>> =
            task {
                let! res = sessions.SendPrompt(childId, promptText, agent)

                match res with
                | Error reason ->
                    disposeSub ()
                    staging?abort (txId) |> ignore
                    do! retire sessions childId
                    return Error reason
                | Ok() -> return! awaitCompletion sessions txId childId completion disposeSub
            }

        let runChild
            (runtime: Runtime)
            (identitySeed: PromptAuthority.IdentitySeed)
            (txId: string)
            (childId: SessionId)
            (kind: BookkeeperRequest)
            (ownerSessionId: SessionId)
            (q: string)
            (a: string)
            (observations: Observation list)
            (extraTranscript: string option)
            : Task<Result<string * string, string>> =
            task {
                let sessions = runtime.Port
                let childKey = SessionId.value childId
                let ownerKey = SessionId.value ownerSessionId
                bookkeeperLedger?bind (childKey, txId, ownerKey)

                let completion =
                    TaskCompletionSource<Result<unit, string>>(TaskCreationOptions.RunContinuationsAsynchronously)

                bookkeeperLedger?registerCompletion (childKey, txId, completion)

                // DSL-MUTABLE: subscription — bookkeeper terminal subscription
                let mutable subscription: System.IDisposable option = None

                subscription <-
                    Some(
                        sessions.SubscribeTerminal(
                            childId,
                            (fun _ outcome ->
                                match outcome with
                                | Ok() -> AsyncSupport.trySetResult completion (Ok()) |> ignore
                                | Error err -> AsyncSupport.trySetResult completion (Error err) |> ignore)
                        )
                    )

                let disposeSub () =
                    subscription |> Option.iter (fun active -> active.Dispose())
                    subscription <- None
                    bookkeeperLedger?removeCompletion (childKey)

                let activeOwner = runtime.ResolveActiveOwner ownerSessionId

                match PromptAuthority.validateInheritedIdentitySeedAgainstActiveOwner activeOwner identitySeed with
                | Error rejection ->
                    disposeSub ()
                    staging?abort (txId) |> ignore
                    do! retire sessions childId
                    return Error(sprintf "bookkeeper identity seed rejected: %A" rejection)
                | Ok participantIdentity ->
                    let promptText = Decisions.envelope kind ownerKey q a observations extraTranscript
                    bookkeeperLedger?authorize (childKey, ParticipantIdentity.selectedAgent participantIdentity, promptText) |> ignore

                    return!
                        sendBookkeeperPrompt
                            sessions
                            txId
                            childId
                            completion
                            disposeSub
                            promptText
                            (ParticipantIdentity.selectedAgent participantIdentity)
            }

        let activeOwnerProfile (runtime: Runtime) (ownerSessionId: SessionId) =
            match runtime.ResolveActiveOwner ownerSessionId with
            | Some profile -> Ok profile
            | None -> Error "bookkeeper owner has no active authority profile"

        let runWithRuntime
            (runtime: Runtime)
            (kind: BookkeeperRequest)
            (ownerSessionId: SessionId)
            (q: string)
            (a: string)
            (observations: Observation list)
            (extraTranscript: string option)
            : Task<Result<string * string, string>> =
            task {
                match
                    activeOwnerProfile runtime ownerSessionId
                    |> Result.bind (fun profile ->
                        PromptAuthority.issueInheritedIdentitySeed Decisions.canonicalAgent profile
                        |> Result.mapError (sprintf "invalid bookkeeper identity seed: %A"))
                with
                | Error error -> return Error error
                | Ok identitySeed ->
                    let txId = Guid.NewGuid().ToString("N")
                    staging?beginTransaction (txId, q, a) |> ignore

                    match!
                        runtime.Port.CreateSiblingSession(
                            ownerSessionId,
                            "bookkeeper:" + txId,
                            Decisions.canonicalAgent
                        )
                    with
                    | Error error ->
                        staging?abort (txId) |> ignore
                        return Error error
                    | Ok childId ->
                        return!
                            runChild
                                runtime
                                identitySeed
                                txId
                                childId
                                kind
                                ownerSessionId
                                q
                                a
                                observations
                                extraTranscript
            }

    let setPort
        (port: ICasebookSessionPort)
        (resolveActiveOwner: SessionId -> PromptAuthority.AuthorityExecutionProfile option)
        : unit =
        lock runtimeGate (fun () ->
            runtime <-
                Some
                    { Port = port
                      ResolveActiveOwner = resolveActiveOwner })

    let resetRuntime () : unit =
        lock runtimeGate (fun () -> runtime <- None)
        bookkeeperLedger?clear ()

    let bindSession (sessionId: string) (txId: string) (ownerSessionId: string) : unit =
        bookkeeperLedger?bind (sessionId, txId, ownerSessionId)

    let unbindSession (sessionId: string) : unit =
        bookkeeperLedger?unbind (sessionId)

    let tryConsumePromptAuthorization
        (sessionId: SessionId)
        (explicitAgent: string option)
        (text: string option)
        : bool =
        match explicitAgent, text with
        | Some agent, Some payload ->
            unbox<bool> (bookkeeperLedger?tryConsume (SessionId.value sessionId, agent, payload))
        | _ -> false

    let completePhysical (sessionId: SessionId) (outcome: Result<unit, string>) : unit =
        bookkeeperLedger?completePhysical (SessionId.value sessionId, outcome) |> ignore

    let tryTxId (sessionId: string) : string option =
        let res: string = unbox<string> (bookkeeperLedger?txIdFor (sessionId))
        if String.IsNullOrEmpty res then None else Some res

    let txIdFor (sessionId: string) : string =
        unbox<string> (bookkeeperLedger?txIdFor (sessionId))

    let isAttached (sessionId: string) : bool =
        unbox<bool> (bookkeeperLedger?isAttached (sessionId))

    let runTransaction
        (kind: BookkeeperRequest)
        (ownerSessionId: SessionId)
        (q: string)
        (a: string)
        (observations: Observation list)
        (extraTranscript: string option)
        : Task<Result<string * string, string>> =
        task {
            let activeRuntime = lock runtimeGate (fun () -> runtime)
            match activeRuntime with
            | None -> return Error "bookkeeper runtime unavailable"
            | Some active ->
                return! Coordination.runWithRuntime active kind ownerSessionId q a observations extraTranscript
        }
