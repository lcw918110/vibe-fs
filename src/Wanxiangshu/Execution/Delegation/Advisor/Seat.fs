namespace Wanxiangshu.Execution.Delegation.Advisor

open System
open FsToolkit.ErrorHandling
open System.Collections.Generic
open System.Threading.Tasks
open Fable.Core
open Fable.Core.JsInterop
open Wanxiangshu.Composition.Durable
open Wanxiangshu.Foundation
open Wanxiangshu.Foundation.Identity
open Wanxiangshu.Interaction.Authority
open Wanxiangshu.Interaction.Dispatch
open Wanxiangshu.Foundation.HostSession
open Wanxiangshu.Participant.ManagedAgents
open Wanxiangshu.Foundation.TerminalSignal
open Wanxiangshu.Persistence.Journal
open Wanxiangshu.Process
open Wanxiangshu.Repository.Programming.Js

/// DSL-class: PhysicalHandle — `Sessions` owns the advisor child and its terminal
/// subscription, `Journal`/`Snapshot` are the read-back capabilities, `Directory`/
/// `Participant` are the child's physical dispatch coordinates; owner delegation.
type AdvisorSeatDeps =
    { Sessions: ISessionHostPort
      Snapshot: ISessionSnapshotPort option
      Journal: AgentJournal option
      Directory: string option
      Participant: string
      BindDelegation: string -> string -> unit }

/// advisor-[001]: one caller session's dedicated advisor seat.
///
/// The first `consult` creates the seat's advisor child through
/// `ISessionHostPort.CreateChildSession`, so the physical parent is the family root
/// (HOST-015: a sub-agent's advisor is a sibling of its owner, never a grandchild), and
/// every later consult reuses that same child but opens a fresh root: a settled (or
/// voided) consult voids the child's logical run (F39-02), so the next consult's view
/// prompt is a new authority root rather than a continuation of a finished run.
///
/// The seat registry below is PROCESS-LIVE and owns no durable fact: a restart loses every
/// seat and the next consult creates a fresh advisor child. The durable link and restart
/// adoption described in `requirements/advisor/WHAT.md` phase 2 are deliberately not part
/// of this contract.
///
/// A consult sends a single managed prompt containing `# intent` followed by caller `[[context]]`,
/// waits for the child's natural terminal, and returns that terminal's assistant prose — never the
/// child's full session view. A terminal that never arrives, a terminal that is not a completed
/// run, or an empty terminal refuses with "advisor did not answer": the seat never fabricates
/// advice (advisor-[004]).
[<RequireQualifiedAccess>]
module AdvisorSeat =

    /// Stable refusal: the advisor produced no prose for this consult.
    [<Literal>]
    let private DidNotAnswer = "advisor did not answer"

    /// Hard cap on one consult. A terminal that never arrives must not hang the caller's
    /// turn: the seat refuses, and the next consult reuses the same child.
    [<Literal>]
    let private ConsultTimeoutMs = 120_000


    /// AGENT-001: the managed agent this seat's child runs as.
    let private advisorAgent = ManagedAgent.nameOf Role.Advisor

    /// advisor-[030] consult fence. A consult answers exactly the intent prompt it sent, so
    /// the fence is bound to that prompt's identity (F39-01). A terminal names the provider
    /// run that reached it (HOST-010/011: the Host assistant message id); the run answers the
    /// physical user message that is its parent, and that message carries the `PromptKey`
    /// the dispatcher stamped on it (snapshot metadata, else the journal's physical landing).
    /// `PriorRun` is the run the previous consult on this child settled on, so a replayed or
    /// re-delivered Completed of an earlier consult is refused even before attribution.
    type private ConsultFence =
        { ChildId: SessionId
          PriorRun: ProviderRunIdentity option }

    module private AdvisorLibrary =
        [<Import("advisorSeatRegistry", "wanxiangli/advisor")>]
        let advisorSeatRegistry: obj = jsNative

        [<Import("settledRunLedger", "wanxiangli/advisor")>]
        let settledRunLedger: obj = jsNative

    open AdvisorLibrary

    /// 已结算运行单调账本：代理至 wanxiangli/advisor 的 settledRunLedger
    let private recordSettled (childId: SessionId) (providerRun: ProviderRunIdentity) : unit =
        settledRunLedger?recordSettled (SessionId.value childId, ProviderRunIdentity.value providerRun)

    let private priorRun (childId: SessionId) : ProviderRunIdentity option =
        let found: string = settledRunLedger?priorRun (SessionId.value childId)
        if String.IsNullOrEmpty found then
            None
        else
            Some(ProviderRunIdentity.create found)

    let private createNewAdvisorChild (deps: AdvisorSeatDeps) (callerId: SessionId) (options: ChildSessionOptions) =
        task {
            match! deps.Sessions.CreateChildSession(callerId, options) with
            | Ok childId -> return Ok childId
            | Error reason -> return Error(sprintf "advisor seat creation failed: %s" reason)
        }

    let private adoptAdvisorChild (deps: AdvisorSeatDeps) (callerId: SessionId) (options: ChildSessionOptions) (children: ChildSessionInfo list) =
        let matches =
            children |> List.filter (fun child ->
                child.Title = options.Title && child.Agent = Some advisorAgent)
        match matches with
        | [ existing ] -> Task.FromResult(Ok existing.SessionId)
        | _ :: _ -> Task.FromResult(Error "multiple advisor seats already exist for this caller; refusing to create another")
        | [] -> createNewAdvisorChild deps callerId options

    let private createAdvisorChild (deps: AdvisorSeatDeps) (callerId: SessionId) : Task<Result<SessionId, string>> =
        task {
            let options: ChildSessionOptions =
                { Title = Some("wanxiangshu:dedi-advisor:" + SessionId.value callerId)
                  Agent = Some advisorAgent
                  Directory = deps.Directory }
            let familyRoot = deps.Sessions.FamilyRootOf callerId
            let! listed = deps.Sessions.ListChildren familyRoot
            match listed with
            | Error reason -> return Error("advisor seat adoption requires physical child listing: " + reason)
            | Ok children -> return! adoptAdvisorChild deps callerId options children
        }

    /// 席位单飞创建与失败锁存：代理至 wanxiangli/advisor 的 advisorSeatRegistry
    let private ensureSeatChild (deps: AdvisorSeatDeps) (callerSessionId: string) : Task<Result<SessionId, string>> =
        task {
            let startChild () =
                task {
                    let! created = createAdvisorChild deps (SessionId.create callerSessionId)
                    return
                        match created with
                        | Ok childId -> Ok(SessionId.value childId)
                        | Error reason -> Error reason
                }

            let! res = advisorSeatRegistry?ensureSeatChild (callerSessionId, startChild)

            return
                match res with
                | Ok childStr -> Ok(SessionId.create childStr)
                | Error reason -> Error reason
        }

    /// The prompt a physical user message carries: the dispatcher's key stamped in the Host
    /// snapshot first, the journal's accepted physical landing as its fallback.
    let private promptOfUserMessage
        (journal: AgentJournal)
        (childId: SessionId)
        (messages: SessionMessage list)
        (userMessageId: string)
        : string option =
        let stamped =
            messages
            |> List.tryFind (fun message -> message.Id = userMessageId)
            |> Option.bind (fun message -> message.PromptKey)

        let landed () =
            (AgentJournal.snapshot journal).AgentProjections.Sessions
            |> Map.tryFind childId
            |> Option.bind (fun session -> session.PromptAuthority)
            |> Option.bind (fun authority ->
                Map.tryFind (PhysicalUserMessageId.create userMessageId) authority.PhysicalLandings)
            |> Option.map (fun landing -> PromptKey.value landing.PromptKey)

        stamped |> Option.orElseWith landed

    let private isAssistantRun (runId: string) (message: SessionMessage) : bool =
        message.Id = runId
        && String.Equals(message.Role, "assistant", StringComparison.OrdinalIgnoreCase)

    /// HOST-010/011 attribution: the prompt key of the user message the run's assistant
    /// message answers, or `None` when the Host gives no such evidence.
    let private promptAnsweredBy
        (journal: AgentJournal)
        (childId: SessionId)
        (messages: SessionMessage list)
        (run: ProviderRunIdentity)
        : string option =
        messages
        |> List.tryFind (isAssistantRun (ProviderRunIdentity.value run))
        |> Option.bind (fun message -> message.ParentId)
        |> Option.bind (promptOfUserMessage journal childId messages)

    let private okValue (result: Result<'value, 'error>) : 'value option =
        match result with
        | Ok value -> Some value
        | Error _ -> None

    let private attributeRun
        (deps: AdvisorSeatDeps)
        (journal: AgentJournal)
        (childId: SessionId)
        (run: ProviderRunIdentity)
        : Task<string option> =
        match deps.Snapshot with
        | None -> Task.FromResult None
        | Some snapshot ->
            task {
                let! messages = snapshot.GetMessages childId

                return
                    okValue messages
                    |> Option.bind (fun messages -> promptAnsweredBy journal childId messages run)
            }

    /// advisor-[030]: does an attributed Completed answer this consult's intent? A run that
    /// answers another prompt (this consult's view, an earlier consult's intent) never does.
    /// Without Host attribution the first fresh Completed after the intent was accepted is
    /// taken, which is the only order evidence left.
    let private answersIntent (intentKey: PromptKey) (attributed: string option) (afterIntent: bool) : bool =
        match attributed with
        | Some key -> key = PromptKey.value intentKey
        | None -> afterIntent

    /// advisor-[030]: a Completed of the run an earlier consult already settled on — a
    /// sticky replay or a duplicate delivery — is never this consult's terminal.
    let private isStaleRun (fence: ConsultFence) (result: AgentRunResult) : bool =
        let isStale: bool =
            settledRunLedger?isStale (SessionId.value fence.ChildId, ProviderRunIdentity.value result.ProviderRun)
        isStale || (fence.PriorRun |> Option.exists (fun prior -> prior = result.ProviderRun))

    /// F39-02: close the advisor child's logical run once this consult is over.
    ///
    /// The child keeps its identity, but its logical run is finished: a follow-up
    /// consult must open a fresh root instead of appending to a completed run
    /// (which has no committed model-routing lease and would fail closed with
    /// EMR-010). The durable `ChildRunVoided` fact is the existing termination
    /// evidence; the dispatcher reads the durable projection, so the close has to
    /// be durable, not process-local.
    let private voidChildRun
        (journal: AgentJournal)
        (callerSessionId: string)
        (childId: SessionId)
        : Task<Result<unit, string>> =
        task {
            let active = (PromptJournalAdapter.create journal).ProjectionFor childId

            match active.ActiveLogicalRun with
            | None -> return Ok()
            | Some _ ->
                let fact =
                    Wanxiangshu.Execution.Delegation.ExecutionFact.ChildRunVoided
                        {| ParentSessionId = SessionId.create callerSessionId
                           ChildSessionId = childId |}

                let! appended = AgentJournal.appendAgent (StreamId.Session childId) None fact journal

                return
                    appended
                    |> Result.map ignore
                    |> Result.mapError (fun failure -> sprintf "advisor consult could not close its run: %A" failure)
        }

    let private executeConsult
        (sendPrompt: string -> Task<Result<PromptKey, string>>)
        (acceptIntent: PromptKey -> unit)
        (completion: TaskCompletionSource<Result<string, string>>)
        (fullPrompt: string)
        : Task<Result<string, string>> =
        taskResult {
            let! key = sendPrompt fullPrompt
            acceptIntent key

            let! arrived =
                NodeTiming.raceExit (completion.Task :> Task) ConsultTimeoutMs
                |> TaskResultCE.ofTask

            do! Result.requireTrue DidNotAnswer arrived
            // advisor-[003]: the consult result is the advisor's final assistant prose,
            // not its post-scope context. An empty terminal is "did not answer".
            let! prose = completion.Task
            do! Result.requireFalse DidNotAnswer (String.IsNullOrWhiteSpace prose)
            return prose
        }

    let private runConsult
        (deps: AdvisorSeatDeps)
        (journal: AgentJournal)
        (callerSessionId: string)
        (childId: SessionId)
        (intent: string)
        : Task<Result<string, string>> =
        task {
            let dispatcher = PromptDispatcher.forPrompts (PromptJournalAdapter.create journal)
            let port = DispatchSessionPort.ofSessionPort deps.Sessions

            let issueIdentitySeed () =
                PromptAuthorityProjectionQueries.issueCurrentOwnerIdentitySeed
                    (AgentJournal.snapshot journal).AgentProjections
                    (SessionId.create callerSessionId)
                    deps.Participant

            let completion =
                TaskCompletionSource<Result<string, string>>(TaskCreationOptions.RunContinuationsAsynchronously)


            let fence =
                { ChildId = childId
                  PriorRun = priorRun childId }

            // DSL-MUTABLE: resource — consult-local fence state. `armed` turns on just before
            // the first send, so the subscribe-time sticky replay (an earlier consult's
            // terminal) is observation only; `phase` says which prompt of the consult is
            // in flight; `intentKey` is the accepted intent prompt; `early` holds fresh
            // Completeds seen before the intent key was known.
            let armed = ref false
            let intentKey: PromptKey option ref = ref None
            let early = ResizeArray<AgentRunResult>()

            let settle (result: AgentRunResult) (key: PromptKey) (afterIntent: bool) : Task<unit> =
                task {
                    let! attributed = attributeRun deps journal childId result.ProviderRun

                    if
                        answersIntent key attributed afterIntent
                        && AsyncSupport.trySetResult completion (Ok result.TerminalText)
                    then
                        recordSettled childId result.ProviderRun
                }

            let admitCompleted (result: AgentRunResult) : unit =
                match intentKey.Value with
                | Some key -> settle result key true |> ignore
                | None -> lock early (fun () -> early.Add result)

            let observeCompleted (result: AgentRunResult) : unit =
                if armed.Value && not (isStaleRun fence result) then
                    admitCompleted result

            // live terminal listener, and the listener must outlive both sends.
            let onTerminal _ outcome =
                match outcome with
                | Completed result when armed.Value && not (isStaleRun fence result) -> observeCompleted result
                | Completed _ -> ()
                | Aborted _
                | Failed _ when armed.Value -> AsyncSupport.trySetResult completion (Error DidNotAnswer) |> ignore
                | _ -> ()

            use _subscription = deps.Sessions.SubscribeTerminal(childId, onTerminal)

            let sendManaged text =
                dispatcher.SendManagedAssignment port childId text issueIdentitySeed deps.Directory None

            // DSL-MUTABLE: resource — acceptance subscriptions the root send attaches; disposed
            // with the consult.
            let acceptance = ResizeArray<IDisposable>()

            let acceptanceObserver: ContinuationAcceptanceObserver =
                { Notify = ignore
                  AttachDisposable = fun subscription -> lock acceptance (fun () -> acceptance.Add subscription)
                  Budget = None }

            // advisor-[002] on a fresh seat (F39-01): the view opens the child's Authority Root,
            // so the dispatcher must see that root accepted before the intent is sent. A
            // detached root send returns before the Host admits it, and the intent would then be
            // claimed as a second AgentOwnerRoot, which chat admission rejects
            // (ActiveRunIdentityConflict). The root send therefore awaits physical acceptance,
            // as the fork's first prompt does.
            //
            // F39-02: the same rule governs the intent prompt. The view's run is closed before
            // the intent is sent, so the intent finds no live logical run and opens a fresh
            // root of its own — with a committed model-routing lease — instead of continuing a
            // finished run (EMR-010).
            let sendRoot (text: string) : Task<Result<PromptKey, string>> =
                match issueIdentitySeed () with
                | Error reason -> Task.FromResult(Error reason)
                | Ok identitySeed ->
                    dispatcher.SendAgentOwnerRoot
                        port
                        childId
                        text
                        identitySeed
                        deps.Directory
                        PromptDispatcher.AwaitMode.Await
                        (Some acceptanceObserver)

            /// A prompt joins the child's live logical run when it has one, and opens a fresh
            /// root when it has none.
            let sendPrompt (text: string) : Task<Result<PromptKey, string>> =
                match (dispatcher.ProjectionFor childId).ActiveLogicalRun with
                | Some _ -> sendManaged text
                | None -> sendRoot text

            let disposeAcceptance () =
                let subscriptions = lock acceptance (fun () -> Seq.toList acceptance)

                for subscription in subscriptions do
                    subscription.Dispose()

            let acceptIntent (key: PromptKey) : unit =
                intentKey.Value <- Some key
                let seen = lock early (fun () -> Seq.toList early)
                // A Completed seen before the intent was accepted answers it only on Host
                // attribution (the view prompt's own run finishes first in that window).
                for result in seen do
                    settle result key false |> ignore

            armed.Value <- true
            let fullPrompt = LlmFacing.instruction intent |> LlmFacing.render

            try
                return! executeConsult sendPrompt acceptIntent completion fullPrompt
            finally
                disposeAcceptance ()
        }

    /// advisor-[004]: an unexpected failure is a refusal like any other — the caller surface
    /// stays `Result`-valued and no error path ever invents advice.
    let private attempt (work: unit -> Task<Result<string, string>>) : Task<Result<string, string>> =
        task {
            try
                return! work ()
            with ex ->
                return Error(sprintf "advisor consult failed: %s" ex.Message)
        }


    /// One settle point for every consult outcome: a Completed answer, an aborted or
    /// failed terminal, a timeout, a refused view, a send failure and an unexpected
    /// exception all leave the child's logical run closed, so no unfinishable run
    /// stays open and the next consult roots freshly.
    let private runConsultAndVoid
        (deps: AdvisorSeatDeps)
        (journal: AgentJournal)
        (callerSessionId: string)
        (childId: SessionId)
        (intent: string)
        : Task<Result<string, string>> =
        task {
            let! outcome = attempt (fun () -> runConsult deps journal callerSessionId childId intent)

            let! closed = voidChildRun journal callerSessionId childId

            match closed with
            | Ok() -> return outcome
            | Error reason -> return Error reason
        }


    let private requireJournal (deps: AdvisorSeatDeps) : Result<AgentJournal, string> =
        match deps.Journal with
        | Some journal -> Ok journal
        | None -> Error "No journal: an advisor consult cannot be claimed"

    let private openSeat
        (deps: AdvisorSeatDeps)
        (journal: AgentJournal)
        (callerSessionId: string)
        (intent: string)
        : Task<Result<string, string>> =
        task {
            let! created = ensureSeatChild deps callerSessionId

            match created with
            | Error reason -> return Error reason
            | Ok childId ->
                deps.BindDelegation (SessionId.value childId) callerSessionId
                return! runConsultAndVoid deps journal callerSessionId childId intent
        }

    let private verifyAdvisorCaller (journal: AgentJournal) (callerSessionId: string) =
        let sessions = (AgentJournal.snapshot journal).AgentProjections
        let caller = SessionId.create callerSessionId
        let owner =
            PromptAuthorityProjectionQueries.activeProfile caller sessions
            |> Option.orElseWith (fun () -> PromptAuthorityProjectionQueries.lastAuthorityProfile caller sessions)
        match owner with
        | Some profile when profile.CanonicalRole = Role.Advisor ->
            Error "A dedicated Advisor cannot open another dedicated Advisor seat"
        | _ -> Ok ()

    let private consultAfterJournal (deps: AdvisorSeatDeps) (callerSessionId: string) (intent: string) (journal: AgentJournal) =
        task {
            match verifyAdvisorCaller journal callerSessionId with
            | Error reason -> return Error reason
            | Ok () -> return! openSeat deps journal callerSessionId intent
        }

    let private consultCore
        (deps: AdvisorSeatDeps)
        (callerSessionId: string)
        (intent: string)
        : Task<Result<string, string>> =
        task {
            match requireJournal deps with
            | Error reason -> return Error reason
            | Ok journal -> return! consultAfterJournal deps callerSessionId intent journal
        }


    let consult (deps: AdvisorSeatDeps) (callerSessionId: string) (intent: string) : Task<Result<string, string>> =
        attempt (fun () -> consultCore deps callerSessionId intent)

    let clear (callerSessionId: string) : unit =
        let maybeChildId: string option = advisorSeatRegistry?clear callerSessionId
        match maybeChildId with
        | Some childId -> settledRunLedger?retire childId
        | None -> ()

    let clearAll () : unit =
        advisorSeatRegistry?clearAll ()
        settledRunLedger?clearAll ()
