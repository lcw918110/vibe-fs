namespace Wanxiangshu.Host.SecondHost

open System
open System.Threading.Tasks
open Fable.Core
open Fable.Core.JsInterop
open Wanxiangshu.Foundation
open Wanxiangshu.Foundation.Identity
open Wanxiangshu.Host
open Wanxiangshu.Host.Ports
open Wanxiangshu.Host.Domain
open Wanxiangshu.Participant.Provider
open Wanxiangshu.Mission.Lifecycle
open Wanxiangshu.Persistence.Journal
open Wanxiangshu.Resources
open Wanxiangshu.Git
open Wanxiangshu.Process

type IClockPort = Wanxiangshu.Foundation.IClockPort
type ITimerPort = Wanxiangshu.Foundation.ITimerPort

/// The product composition root for the second host (HNI P-11).
///
/// The host supplies ports; this module supplies the product behaviour:
/// a durable work opening, a durable Ownership scope, the paired-bilingual
/// system documents, one product tool, and the neutral product gates that
/// have no second-host binding yet.
module HostPlugin =

    let nodeClockPort () : IClockPort =
        Wanxiangshu.Process.NodeTiming.nodeClockPort ()

    let nodeTimerPort () : ITimerPort =
        Wanxiangshu.Process.NodeTiming.nodeTimerPort ()

    [<Emit("JSON.parse($0)")>]
    let private parseJson (_text: string) : obj = jsNative

    [<Emit("$0 && typeof $0.text === 'string' ? $0.text : ''")>]
    let private jsonTextField (_value: obj) : string = jsNative

    /// The product default role for a root session: the Engineer law. The
    /// product's retired roles carry no provider prompt, so they fall back
    /// to the same default rather than to a hand-written document.
    let private defaultRole = Role.Engineer

    /// A parsed role that still carries a product prompt. A retired role is
    /// rejected by the product's role resource lookup; those fall back to
    /// the default role.
    let private liveRole (role: Role) : Role =
        try
            PromptResources.systemForRole ProviderLanguage.English role |> ignore
            role
        with _ ->
            defaultRole

    /// A role label that the product's role vocabulary still knows.
    /// An unknown label uses the product default role.
    let private parsedRole (label: string) : Role =
        match Roles.tryParseRole label with
        | Some role -> liveRole role
        | None -> defaultRole

    /// The role of the turn context, resolved to the product's role law.
    /// An unknown or retired role label uses the product default role.
    let private roleOf (context: TurnContext) : Role =
        match context.Role with
        | Some(AgentRole label) -> parsedRole label
        | None -> defaultRole

    /// The paired-bilingual tool description. The English and the Chinese
    /// line both come from the product's own resource tree
    /// (`resources/provider/<semantic>/<en.md|zh-CN.md>`), never hand-written.
    let private bilingualDescription () : string =
        let tryRead (language: ProviderLanguage) (semanticPath: string) =
            try
                Some((ProviderResources.readText language semanticPath).Trim())
            with _ ->
                None

        let candidates = [ "tool/deliver"; "tool/join/description" ]

        let chosen =
            candidates
            |> List.tryPick (fun semanticPath ->
                match
                    tryRead ProviderLanguage.English semanticPath,
                    tryRead ProviderLanguage.SimplifiedChinese semanticPath
                with
                | Some english, Some chinese when
                    not (String.IsNullOrWhiteSpace english)
                    && not (String.IsNullOrWhiteSpace chinese)
                    ->
                    Some(english, chinese)
                | _ -> None)

        match chosen with
        | Some(english, chinese) -> english + "\n\n" + chinese
        | None ->
            // No product tool description resource resolves for this host.
            // The product's own bilingual role law is used instead of
            // hand-written text; both languages come from the product's
            // resource tree.
            PromptResources.systemForRole ProviderLanguage.English defaultRole
            + "\n\n"
            + PromptResources.systemForRole ProviderLanguage.SimplifiedChinese defaultRole

    /// Deterministic work id for a session or a workspace.
    /// The second host has no host-supplied session identity at activation
    /// time, so the work identity is derived from the workspace digest; the
    /// child session opened by the deliver tool derives its own work id from
    /// its session id. Both are stable across activations of the same
    /// workspace and of the same session.
    let private workIdOf (seed: string) : string =
        "host2-" + (HostDigest.sha256Hex seed).Substring(0, 32)

    /// The text body of one transcript message.
    let private messageBody (message: TranscriptMessage) : string =
        message.Parts
        |> List.collect (fun part ->
            match part with
            | TranscriptPart.Text value -> [ value ]
            | _ -> [])
        |> String.concat ""

    /// The assistant reply of a child session: the last assistant message
    /// that carries a terminal state and non-empty text.
    let private assistantReply (messages: TranscriptMessage list) : string option =
        messages
        |> List.filter (fun message -> message.Role = TranscriptRole.Assistant && message.Terminal.IsSome)
        |> List.tryPick (fun message ->
            let text = messageBody message |> fun value -> value.Trim()

            if String.IsNullOrWhiteSpace text then None else Some text)

    /// A failed product tool outcome.
    let private failureOutcome (text: string) : ToolOutcome =
        { Text = text
          Truncated = false
          IsError = true
          Details = None }

    /// A successful product tool outcome.
    let private successOutcome (text: string) : ToolOutcome =
        { Text = text
          Truncated = false
          IsError = false
          Details = None }

    /// The text of a delivery request, read from the raw invocation payload.
    /// A malformed payload carries no text.
    let private textFieldOf (raw: string) : string =
        try
            jsonTextField (parseJson raw)
        with _ ->
            ""

    /// The text a tool invocation asks to deliver.
    let private invocationText (invocation: ToolInvocation) : string =
        let (CanonicalJsonText raw) = invocation.Args
        textFieldOf raw

    /// The observation of the child's transcript: what was seen, and the
    /// terminal reply if the transcript already carries one.
    let private observeTranscript (messages: Result<TranscriptMessage list, HostError>) : string * string option =
        match messages with
        | Ok observed ->
            ("observed "
             + string observed.Length
             + " message(s) in the child transcript; no terminal assistant reply yet",
             assistantReply observed)
        | Error error -> ("the child transcript could not be read: " + string error, None)

    /// The terminal outcome of one transcript observation, if the wait for
    /// the child's reply is over.
    let private terminalOutcome
        (clock: IClockPort)
        (reply: string option)
        (observation: string)
        (deadline: Deadline)
        : ToolOutcome option =
        let expired = Deadline.isExpired clock.UtcNow deadline

        match reply with
        | Some text -> Some(successOutcome text)
        | None when expired ->
            Some(
                failureOutcome (
                    "wxs_deliver timed out after 120s waiting for the child session's terminal reply; "
                    + observation
                )
            )
        | None -> None

    let private waitPoll (clock: IClockPort) (timer: ITimerPort) (deadline: Deadline) : Task<unit> =
        task {
            let delay = timer.Delay(min 250 (Deadline.nextWaitMs clock.UtcNow deadline))

            try
                do! delay.Delay
            finally
                delay.Cancel()
        }

    /// The wait for the child's terminal reply: the child's transcript is
    /// polled until it carries a terminal reply or the deadline is reached.
    let private pollTranscript
        (clock: IClockPort)
        (timer: ITimerPort)
        (ports: HostPorts)
        (childSession: SessionId)
        (deadline: Deadline)
        : Task<ToolOutcome> =
        let rec poll () : Task<ToolOutcome> =
            task {
                let! messages = ports.Transcript.Messages childSession

                let observation, reply = observeTranscript messages

                let terminal = terminalOutcome clock reply observation deadline

                match terminal with
                | Some outcome -> return outcome
                | None ->
                    do! waitPoll clock timer deadline
                    return! poll ()
            }

        poll ()

    /// The child's terminal reply, or the timeout outcome when it never
    /// arrives.
    let private awaitChildReply
        (clock: IClockPort)
        (timer: ITimerPort)
        (ports: HostPorts)
        (childSession: SessionId)
        : Task<ToolOutcome> =
        task {
            let deadline = Deadline.ofBudget (clock.UtcNow()) (TimeSpan.FromSeconds 120.0)
            let! outcome = pollTranscript clock timer ports childSession deadline
            return outcome
        }

    /// The delivery of one message to the child session.
    let private sendToChild
        clock
        timer
        (ports: HostPorts)
        (childSession: SessionId)
        (text: string)
        : Task<ToolOutcome> =
        task {
            let! sent =
                ports.Prompts.Send(
                    childSession,
                    { Text = text
                      Route = None
                      Role = None
                      Workspace = None
                      Correlation = None
                      ToolPolicy = None },
                    None
                )

            match sent with
            | Outcome.Fatal reason -> return failureOutcome ("wxs_deliver could not deliver the message: " + reason)
            | Outcome.Retryable reason -> return failureOutcome ("wxs_deliver could not deliver the message: " + reason)
            | Outcome.AcceptanceUnknown reason ->
                return failureOutcome ("wxs_deliver could not confirm the delivery: " + reason)
            | _ -> return! awaitChildReply clock timer ports childSession
        }

    /// 子会话的材料交还不依赖生命周期 Open。第一次 review 才开立工作。
    let private openChildWork (stateRoot: string) (root: string) (childWorkId: string) : Result<unit, string> =
        ignore stateRoot
        ignore root

        if String.IsNullOrWhiteSpace childWorkId then
            Error "child work id cannot be empty"
        else
            Ok()

    /// The durable work of the host's own root session. The activation scope
    /// is opened against the SAME work id, so the scope and the work are one
    /// durable fact, not two.
    let private activationWorkId (ports: HostPorts) (root: string) : string =
        workIdOf (ports.Paths.ProjectId + "|" + root)

    /// The scope that owns this host's activation work. It is a durable fact
    /// in the ownership registry, not an in-memory flag.
    let private activationScopeId (ports: HostPorts) : string =
        "host-activation:" + ports.Paths.ProjectId

    /// 激活不写生命周期 Open。材料交还与归属范围仍在后面单独建立。
    let private openActivationWork (stateRoot: string) (workId: string) (root: string) : Result<unit, HostError> =
        if
            String.IsNullOrWhiteSpace stateRoot
            || String.IsNullOrWhiteSpace workId
            || String.IsNullOrWhiteSpace root
        then
            Error(HostError.Transport "the second host activation failed: the machine state directory is missing")
        else
            Ok()

    /// Open the ownership scope that owns this host's activation work. The
    /// scope is a durable fact in the ownership registry, not an in-memory
    /// flag.
    let private openActivationScope (ports: HostPorts) (stateRoot: string) : Result<unit, HostError> =
        let registryId = "host-activation"
        let scopeId = activationScopeId ports

        let receipt =
            DurableWorkHost.executeOwnershipDurable
                stateRoot
                registryId
                (scopeId + ":open")
                (DurableWorkHost.ownershipMessage "OpenScope" [ "scopeId", box scopeId; "parentId", null ])

        let committed =
            receipt.Status = "Committed"
            || receipt.Status = "Replayed"
            // An idempotent re-opening of the same scope is an observation of
            // the same durable scope, not a failure.
            || (receipt.Status = "Observed" && receipt.Kind = "ScopeOpened")

        let detail =
            if String.IsNullOrWhiteSpace receipt.Reason then
                ""
            else
                " (" + receipt.Reason + ")"

        if committed then
            Ok()
        else
            Error(
                HostError.Transport(
                    "the second host activation failed to open its ownership scope: "
                    + receipt.Status
                    + detail
                )
            )

    /// 材料交还不要求生命周期已经 Open。会话身份非空即可投递。
    let private ownsActivationWork (stateRoot: string) (sessionId: string) : bool =
        ignore stateRoot
        not (String.IsNullOrWhiteSpace sessionId)

    /// Open the durable work of a session the host announces. Every session
    /// that talks to this product gets its own durable work, so the tool
    /// admission can be answered from the journal instead of from a flag.
    let private openSessionWork (stateRoot: string) (root: string) (sessionId: string) : unit =
        if not (String.IsNullOrWhiteSpace sessionId) then
            openChildWork stateRoot root (workIdOf sessionId) |> ignore

    /// The delivery to a child session whose durable work is already open.
    let private deliverToChildSession
        clock
        timer
        (ports: HostPorts)
        (stateRoot: string)
        (root: string)
        (text: string)
        (childSession: SessionId)
        : Task<ToolOutcome> =
        task {
            let childWorkId = workIdOf (Identity.SessionId.value childSession)

            match openChildWork stateRoot root childWorkId with
            | Error reason ->
                return failureOutcome ("wxs_deliver could not open the child session's durable work: " + reason)
            | Ok() -> return! sendToChild clock timer ports childSession text
        }

    /// The child session that carries the delivery.
    let private createChild (ports: HostPorts) (parent: SessionId option) : HostResult<SessionId> =
        ports.Sessions.Create(
            parent,
            { Title = Some "wxs_deliver"
              Role = None
              Workspace = None }
        )

    /// The delivery of one message to a newly created child session.
    let private deliverToChild
        clock
        timer
        (ports: HostPorts)
        (stateRoot: string)
        (root: string)
        (text: string)
        (parent: SessionId option)
        : Task<ToolOutcome> =
        task {
            let! child = createChild ports parent

            match child with
            | Error err -> return failureOutcome ("wxs_deliver could not create the child session: " + string err)
            | Ok childSession ->
                parent
                |> Option.iter (fun p ->
                    Wanxiangshu.Context.Scope.Surface.bindDelegation
                        (Identity.SessionId.value childSession)
                        (Identity.SessionId.value p))

                return! deliverToChildSession clock timer ports stateRoot root text childSession
        }

    let private snapshotForbidText (sessionText: string) : string =
        ProviderProse.render
            (ProviderLanguageBinding.forSessionText sessionText)
            "delegation/snapshot-dialogue/forbid"
            Map.empty

    let private negotiatePromptText (delegator: string) (questions: string) : string =
        let cleaned =
            if String.IsNullOrWhiteSpace questions then
                "Please clarify your intent."
            else
                questions.Trim()

        "<btw>\n"
        + "The assistant currently executing the task on your behalf requires clarification:\n"
        + cleaned
        + "\n\n"
        + snapshotForbidText delegator
        + "\n</btw>"

    let negotiateRetryText (delegator: string) (questions: string) : string =
        let cleaned =
            if String.IsNullOrWhiteSpace questions then
                "Please clarify your intent."
            else
                questions.Trim()

        "<btw>\n"
        + cleaned
        + "\n\n"
        + snapshotForbidText delegator
        + "\n</btw>"

    let private extractQuestions (args: CanonicalJsonText) : string =
        let (CanonicalJsonText raw) = args

        try
            let parsed: obj = Fable.Core.JS.JSON.parse raw
            let q: obj = Fable.Core.JsInterop.emitJsExpr (parsed, "questions") "$0[$1]"
            if isNull q then "" else string q
        with _ ->
            ""

    let private isDynamicOk (d: obj) : bool =
        let v: obj = Fable.Core.JsInterop.emitJsExpr (d, "ok") "$0[$1]"
        not (isNull v) && unbox<bool> v

    let private dynamicField (d: obj) (name: string) : string =
        let v: obj = Fable.Core.JsInterop.emitJsExpr (d, name) "$0[$1]"
        if isNull v then "" else string v

    /// Best-effort negotiate answer delivery: every failed step is recorded
    /// (receipt, recipient, delegator, stage, reason). Recording only — no
    /// decision reads it.
    let private negotiationDeliveryFailed
        (receipt: string)
        (recipient: string)
        (delegator: string)
        (stage: string)
        (reason: string)
        : unit =
        Diagnostic.emit
            "negotiate-delivery-failed"
            [ "receipt", receipt
              "recipient", recipient
              "delegator", delegator
              "stage", stage
              "reason", (if String.IsNullOrWhiteSpace reason then "unspecified" else reason) ]

    let private processNegotiationDelivery
        (transport: INegotiationTransport)
        (sid: string)
        (delegator: string)
        (readyReceipt: string)
        (reply: string)
        : Task<unit> =
        task {
            let steerReq =
                { SteerDeliveryRequest.RecipientSessionId = sid
                  Receipt = readyReceipt
                  SteerText = reply
                  CorrelationKey = PromptKey.create ("wxs:negotiate:" + readyReceipt) }

            let! deliverRes = transport.DeliverAndObserve steerReq

            match deliverRes with
            | Ok() ->
                let confirmed =
                    Wanxiangshu.Context.Scope.Surface.confirmNegotiationSteer readyReceipt sid

                if not confirmed then
                    negotiationDeliveryFailed
                        readyReceipt
                        sid
                        delegator
                        "confirm-steer"
                        "steer delivered but the receipt was not confirmed (unknown, settled or stale)"
            | Error reason -> negotiationDeliveryFailed readyReceipt sid delegator "deliver-steer" reason
        }

    let private launchNegotiationBackground
        (transport: INegotiationTransport)
        (sid: string)
        (questions: string)
        (delegator: string)
        (receipt: string)
        : unit =
        let run () =
            task {
                let req =
                    { EphemeralTurnRequest.DelegatorSessionId = delegator
                      Receipt = receipt
                      PromptText = negotiatePromptText delegator questions
                      RetryPromptText = negotiateRetryText delegator questions }

                let! ephemeralRes = transport.AskEphemeral req

                let proseOpt =
                    match ephemeralRes with
                    | Ok prose -> Some prose
                    | Error reason ->
                        negotiationDeliveryFailed receipt sid delegator "ask-delegator" reason
                        None

                let prepOpt =
                    proseOpt
                    |> Option.bind (fun prose ->
                        let prep: obj =
                            Wanxiangshu.Context.Scope.Surface.prepareNegotiationReply receipt prose

                        if isDynamicOk prep then
                            Some prep
                        else
                            negotiationDeliveryFailed receipt sid delegator "prepare-reply" (dynamicField prep "reason")
                            None)

                match prepOpt with
                | None -> ()
                | Some prep ->
                    let readyReceipt = receipt
                    let reply = dynamicField prep "steer"
                    do! processNegotiationDelivery transport sid delegator readyReceipt reply
            }

        let guarded () =
            task {
                try
                    do! run ()
                with ex ->
                    negotiationDeliveryFailed receipt sid delegator "exception" ex.Message
            }

        guarded () |> ignore

    let private evaluateNegotiation (transport: INegotiationTransport) (sid: string) (questions: string) : ToolOutcome =
        let admitted: obj = Wanxiangshu.Context.Scope.Surface.askNegotiation sid questions
        let isAdmitted = isDynamicOk admitted
        let reason = dynamicField admitted "reason"
        let delegator = dynamicField admitted "delegator"
        let receipt = dynamicField admitted "receipt"
        let canNegotiate = isAdmitted && transport.CanNegotiate delegator

        if not isAdmitted then
            failureOutcome (if reason = "" then "negotiation refused" else reason)
        elif not canNegotiate then
            negotiationDeliveryFailed
                receipt
                sid
                delegator
                "delegator-context"
                "delegator context is not available for ephemeral turn"

            failureOutcome "Negotiation refused: delegator context is not available for ephemeral turn."
        else
            launchNegotiationBackground transport sid questions delegator receipt
            successOutcome (
                "Clarification requested (receipt "
                + receipt
                + "). Negotiation is complete and the timeline continues; resetScope is available now. Do not wait for an answer; if the delegator's answer is obtained, it may arrive later as a steer."
            )

    /// The plugin value. `Activate` performs the durable product wiring and
    /// returns the product hooks for the second host.
    let private createWithTimerAndTransport
        clock
        (timer: ITimerPort)
        (initialTransport: INegotiationTransport option)
        : IHostPlugin =
        // DSL-MUTABLE: subscription — the omp adapter attaches its negotiation transport after activation and every later message reads the latest one.
        let mutable transportOpt = initialTransport

        { new IHostPlugin with
            member _.Activate(ports: HostPorts) : HostResult<ProductHooks> =
                taskResult {
                    let workspace =
                        match ports.Paths.ProjectRoot with
                        | WorkspacePath path -> path

                    // The product's own resolution of the work tree root and
                    // of the machine state directory. `RuntimePath.stateRoot`
                    // is the resolver used by the product's lifecycle
                    // opening (`Mission/Manager/Workflow.fs`); in this
                    // codebase the durable journal lives in the state root,
                    // so the state root is the journal's gitCommonDir.
                    let! root = GitSubject.revParseShowToplevel workspace |> ofTask

                    if String.IsNullOrWhiteSpace root then
                        return!
                            Error(
                                HostError.Transport
                                    "the second host activation failed: Git did not identify a worktree root"
                            )

                    let! stateRoot = RuntimePath.stateRoot root |> ofTask

                    if String.IsNullOrWhiteSpace stateRoot then
                        return!
                            Error(
                                HostError.Transport
                                    "the second host activation failed: the machine state directory is missing"
                            )

                    do!
                        openActivationWork stateRoot (activationWorkId ports root) root
                        |> Result.bind (fun () -> openActivationScope ports stateRoot)

                    // The one product tool of this host: it hands a message to
                    // a child session and returns the child's reply.
                    let deliverSpec: ToolSpec =
                        { Name = "wxs_deliver"
                          Description = bilingualDescription ()
                          Schema =
                            JsonSchema(
                                "{\"type\":\"object\",\"properties\":{\"text\":{\"type\":\"string\"}},"
                                + "\"required\":[\"text\"]}"
                            )
                          // The tool is admitted by the session's own durable
                          // work: only a session whose work is open in the
                          // journal may deliver. A call without that durable
                          // evidence is refused.
                          Admission =
                            Some(
                                ToolAdmission.PrivateAttachment(fun invocation ->
                                    ownsActivationWork stateRoot (Identity.SessionId.value invocation.Session))
                            )
                          Execute =
                            fun invocation ->
                                task {
                                    let text = invocationText invocation

                                    if String.IsNullOrWhiteSpace text then
                                        return failureOutcome "wxs_deliver requires a non-empty `text` field."
                                    else
                                        return!
                                            deliverToChild
                                                clock
                                                timer
                                                ports
                                                stateRoot
                                                root
                                                text
                                                (Some invocation.Session)
                                } }

                    let negotiateSpec: ToolSpec =
                        { Name = "negotiate"
                          Description = "Ask clarifying questions to the delegating agent via an ephemeral side turn."
                          Schema =
                            JsonSchema(
                                "{\"type\":\"object\",\"properties\":{\"questions\":{\"type\":\"string\"}},\"required\":[\"questions\"]}"
                            )
                          Admission = None
                          Execute =
                            fun (invocation: ToolInvocation) ->
                                task {
                                    match transportOpt with
                                    | None -> return failureOutcome "Negotiation refused: transport unavailable."
                                    | Some transport ->
                                        let sid = Identity.SessionId.value invocation.Session
                                        let questions = extractQuestions invocation.Args
                                        return evaluateNegotiation transport sid questions
                                } }

                    do! ports.Tools.Register [ deliverSpec; negotiateSpec ]

                    let turn: ITurnTransform =
                        { new ITurnTransform with
                            member _.AdmitTurn(_request: TurnRequest) =
                                Task.FromResult(TurnAdmission.Admit None)

                            member _.SystemPrompt(context: TurnContext, system: string list) =
                                task {
                                    // The host's own system documents are kept
                                    // as they are; the product appends its own
                                    // paired-bilingual documents for the role.
                                    let role = roleOf context

                                    let english = PromptResources.systemForRole ProviderLanguage.English role

                                    let chinese = PromptResources.systemForRole ProviderLanguage.SimplifiedChinese role

                                    return system @ [ english; chinese ]
                                }

                            member _.ContextMessages(_context: TurnContext, _messages: ContextMessage list) =
                                Task.FromResult(ContextEdit.Keep)

                            member _.ProviderParams(_context: TurnContext, _parameters: ProviderParams) =
                                Task.FromResult(ParamsVerdict.Accept) }

                    let compaction: ICompactionGate =
                        { new ICompactionGate with
                            member _.OnCompacting(_session: SessionId) =
                                // The product compaction policy
                                // (Host/CompactionPolicy) owns the contribution;
                                // it has no second-host binding yet, so the
                                // neutral default contributes nothing to the
                                // host's compaction.
                                Task.FromResult(CompactionContribution.Nothing)

                            member _.AutoContinue(_session: SessionId) =
                                // The same product owner decides auto-continue;
                                // without a second-host binding the neutral
                                // default lets the host continue.
                                Task.FromResult(ContinueVerdict.Allow) }

                    let onSignal (signal: HostSignal) : Task =
                        task {
                            match signal with
                            | HostSignal.SessionCreated info ->
                                // The host announced a session, so the product
                                // opens that session's durable work. The journal
                                // then holds the evidence the tool admission
                                // reads back.
                                openSessionWork stateRoot root (Identity.SessionId.value info.Id)
                            | _ ->
                                // The product's durable command vocabulary for a
                                // host's tool and attempt facts (ToolStarted,
                                // AttemptFinished) is owned by the product's
                                // per-turn journal; it has no second-host binding
                                // yet, so no durable command kind is issued here.
                                // The fact is not silently dropped: the host's
                                // diagnostic sink records it.
                                ports.Diagnostics.Emit(
                                    "second-host-signal-unmapped",
                                    [ "signal", string signal
                                      "reason",
                                      "no product command kind exists for host tool/attempt facts in the second host" ]
                                )
                        }

                    let dispose () : Task =
                        timer.Dispose()
                        Task.FromResult()

                    return
                        { ToolGate = None
                          Turn = Some turn
                          Compaction = compaction
                          Spawn = None
                          OnSignal = Some onSignal
                          Dispose = dispose }
                }
          interface INegotiationTransportConsumer with
              member _.AttachTransport(transport: INegotiationTransport) = transportOpt <- Some transport }

    [<Emit("new AggregateError([$0, $1], 'Second-host activation and timer cleanup failed', { cause: $0 })")>]
    let private activationCleanupFailure (original: obj) (cleanup: exn) : exn = jsNative

    let private disposeAfterFailure (timer: ITimerPort) (original: obj) =
        try
            timer.Dispose()
        with cleanup ->
            raise (activationCleanupFailure original cleanup)

    let private createInternal
        (clock: IClockPort)
        (createTimer: unit -> ITimerPort)
        (initialTransport: INegotiationTransport option)
        : IHostPlugin =
        // DSL-MUTABLE: subscription — holds the underlying activated plugin instance for post-activation transport attachment.
        let pluginRef: IHostPlugin option ref = ref None
        // DSL-MUTABLE: subscription — caches the attached negotiation transport across Activate calls.
        let transportRef: INegotiationTransport option ref = ref initialTransport

        { new IHostPlugin with
            member _.Activate(ports: HostPorts) =
                task {
                    let timer = createTimer ()
                    let underlying = createWithTimerAndTransport clock timer transportRef.Value
                    pluginRef := Some underlying

                    let! outcome =
                        task {
                            try
                                return! underlying.Activate ports
                            with original ->
                                disposeAfterFailure timer (box original)
                                return raise original
                        }

                    match outcome with
                    | Ok _ -> return outcome
                    | Error original ->
                        disposeAfterFailure timer (box original)
                        return outcome
                }
          interface INegotiationTransportConsumer with
              member _.AttachTransport(transport: INegotiationTransport) =
                  transportRef := Some transport

                  emitJsExpr
                      (pluginRef.Value, transport)
                      "if ($0 && typeof $0.AttachTransport === 'function') { $0.AttachTransport($1); }" }

    let create (clock: IClockPort) (createTimer: unit -> ITimerPort) : IHostPlugin =
        createInternal clock createTimer None

    let createWithTransport
        (clock: IClockPort)
        (createTimer: unit -> ITimerPort)
        (transport: INegotiationTransport)
        : IHostPlugin =
        createInternal clock createTimer (Some transport)
