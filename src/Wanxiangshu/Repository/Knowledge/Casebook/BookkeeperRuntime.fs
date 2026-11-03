namespace Wanxiangshu.Repository.Knowledge.Casebook

open System
open System.Threading.Tasks
open Fable.Core
open Fable.Core.JsInterop
open Wanxiangshu.Foundation
open Wanxiangshu.Foundation.Identity
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

/// Product prompts and host session effects for a library Bookkeeper transaction.
module BookkeeperRuntime =

    [<Import("bookkeeperStaging", "wanxiangli/casebook")>]
    let private staging: obj = jsNative

    [<Import("bookkeeperLedger", "wanxiangli/casebook")>]
    let private bookkeeperLedger: obj = jsNative

    [<Import("BookkeeperTransaction", "wanxiangli/casebook")>]
    let private transactionClass: obj = jsNative

    [<Emit("new $0($1, $2)")>]
    let private construct (cls: obj) (staging: obj) (ledger: obj) : obj = jsNative

    let private transactions = construct transactionClass staging bookkeeperLedger

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

    module private SessionEffects =

        let private unitResult (result: Result<unit, string>) : obj =
            match result with
            | Ok() -> createObj [ "ok" ==> true ]
            | Error error -> createObj [ "ok" ==> false; "error" ==> error ]

        let create
            (runtime: Runtime)
            (identitySeed: PromptAuthority.IdentitySeed)
            (txId: string)
            (kind: BookkeeperRequest)
            (ownerSessionId: SessionId)
            (q: string)
            (a: string)
            (observations: Observation list)
            (extraTranscript: string option)
            : obj =
            let openSession () =
                task {
                    let! opened =
                        runtime.Port.CreateSiblingSession(
                            ownerSessionId,
                            "bookkeeper:" + txId,
                            Decisions.canonicalAgent
                        )

                    return
                        match opened with
                        | Ok childId -> createObj [ "ok" ==> true; "value" ==> SessionId.value childId ]
                        | Error error -> createObj [ "ok" ==> false; "error" ==> error ]
                }

            let subscribe (childId: string) (complete: obj -> unit) =
                let subscription =
                    runtime.Port.SubscribeTerminal(
                        SessionId.create childId,
                        (fun _ outcome -> complete (unitResult outcome))
                    )

                fun () -> subscription.Dispose()

            let prepare (_childId: string) =
                let activeOwner = runtime.ResolveActiveOwner ownerSessionId

                match PromptAuthority.validateInheritedIdentitySeedAgainstActiveOwner activeOwner identitySeed with
                | Error rejection ->
                    createObj
                        [ "ok" ==> false
                          "error" ==> sprintf "bookkeeper identity seed rejected: %A" rejection ]
                | Ok participantIdentity ->
                    let promptText =
                        Decisions.envelope kind (SessionId.value ownerSessionId) q a observations extraTranscript

                    createObj
                        [ "ok" ==> true
                          "value"
                          ==> createObj
                                  [ "agent" ==> ParticipantIdentity.selectedAgent participantIdentity
                                    "text" ==> promptText ] ]

            let send (childId: string) (agent: string) (text: string) =
                task {
                    let! sent = runtime.Port.SendPrompt(SessionId.create childId, text, agent)
                    return unitResult sent
                }

            createObj
                [ "open" ==> openSession
                  "subscribe"
                  ==> Func<string, (obj -> unit), (unit -> unit)>(fun childId complete -> subscribe childId complete)
                  "prepare" ==> prepare
                  "send"
                  ==> Func<string, string, string, Task<obj>>(fun childId agent text -> send childId agent text)
                  "abort" ==> (SessionId.create >> runtime.Port.AbortSession) ]

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

                    let request =
                        createObj
                            [ "txId" ==> txId
                              "ownerId" ==> SessionId.value ownerSessionId
                              "question" ==> q
                              "answer" ==> a ]

                    let effects =
                        create runtime identitySeed txId kind ownerSessionId q a observations extraTranscript

                    let! result = unbox<Task<obj>> (transactions?run (request, effects))

                    if unbox<bool> result?ok then
                        let value: obj = result?value
                        return Ok(unbox value?question, unbox value?answer)
                    else
                        return Error(unbox result?error)
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

    let unbindSession (sessionId: string) : unit = bookkeeperLedger?unbind (sessionId)

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
                return! SessionEffects.runWithRuntime active kind ownerSessionId q a observations extraTranscript
        }
