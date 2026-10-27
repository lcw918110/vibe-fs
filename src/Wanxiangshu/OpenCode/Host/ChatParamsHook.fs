namespace Wanxiangshu.OpenCode

open Wanxiangshu.Execution.Routing

open System
open Fable.Core
open Fable.Core.JsInterop
open Wanxiangshu.Execution.Session.ChatExecution
open Wanxiangshu.Foundation.Identity
open Wanxiangshu.Host.Ports
open Wanxiangshu.Persistence.Journal
open Wanxiangshu.Participant.ManagedAgents

/// PROMPT-006 / execution-model-routing-009: chat.params is an observation barrier, not a routing
/// authority and not a drift checker. chat.message / internal SendPrompt must already have
/// established the lease and projected model+variant before the provider reaches this hook;
/// the hook reads the exact committed lease and only fails closed when a managed run has none.
module ChatParamsHook =

    [<Import("ProviderCalibration", "wanxiangli/provider-calibration")>]
    let private calibration: obj = jsNative

    let private normalizeText text =
        if String.IsNullOrWhiteSpace text then
            None
        else
            Some(text.Trim())

    let private textField (value: obj) (name: string) =
        if isNull value || isNull value?(name) then
            None
        else
            normalizeText (string value?(name))

    let private childObject (value: obj) (name: string) : obj =
        if isNull value then null else value?(name)

    let private trySessionId (input: obj) =
        if isNull input then
            None
        elif not (isNull input?sessionID) then
            normalizeText (string input?sessionID) |> Option.map SessionId.create
        elif not (isNull input?sessionId) then
            normalizeText (string input?sessionId) |> Option.map SessionId.create
        elif not (isNull input?message) && not (isNull input?message?sessionID) then
            normalizeText (string input?message?sessionID) |> Option.map SessionId.create
        elif not (isNull input?info) && not (isNull input?info?sessionID) then
            normalizeText (string input?info?sessionID) |> Option.map SessionId.create
        else
            None

    /// host-boundary-008: decode the exact physical user message id for this
    /// request. The id is the physical user message, never an assistant id.
    let private tryPhysicalUserMessageId (input: obj) =
        if isNull input then
            None
        elif not (isNull input?messageID) then
            normalizeText (string input?messageID)
            |> Option.map PhysicalUserMessageId.create
        elif not (isNull input?messageId) then
            normalizeText (string input?messageId)
            |> Option.map PhysicalUserMessageId.create
        elif not (isNull input?message) && not (isNull input?message?id) then
            normalizeText (string input?message?id)
            |> Option.map PhysicalUserMessageId.create
        elif not (isNull input?info) && not (isNull input?info?id) then
            normalizeText (string input?info?id) |> Option.map PhysicalUserMessageId.create
        else
            None

    /// What the exact committed execution for one physical user message holds.
    /// Read-only projection of owner truth: the hook never writes identity,
    /// never re-routes, never allocates a lease, never checks drift.
    let private readAdmission (key: ChatExecutionKey) = ModelRouting.readExecutionAdmission key

    let private tryAgent (input: obj) =
        [ input; childObject input "info"; childObject input "message" ]
        |> List.tryPick (fun candidate -> textField candidate "agent")

    let private supportsTemperature (input: obj) =
        if
            isNull input
            || isNull input?model
            || isNull input?model?capabilities
            || isNull input?model?capabilities?temperature
        then
            true
        else
            input?model?capabilities?temperature <> box false

    let private applyManagedTemperature (input: obj) (output: obj) =
        if supportsTemperature input then
            emitJsStatement
                (input, output, 1.0)
                """
                if ($1 && typeof $1 === 'object') {
                    $1.temperature = $2;
                    if ($1.options && typeof $1.options === 'object') {
                        $1.options.temperature = $2;
                    }
                }
                if ($0 && typeof $0 === 'object' && $0.model && typeof $0.model === 'object') {
                    if (!$0.model.options || typeof $0.model.options !== 'object') {
                        $0.model.options = {};
                    }
                    $0.model.options.temperature = $2;
                    if ($0.model.variants && typeof $0.model.variants === 'object') {
                        for (const k of Object.keys($0.model.variants)) {
                            if ($0.model.variants[k] && typeof $0.model.variants[k] === 'object') {
                                $0.model.variants[k].temperature = $2;
                            }
                        }
                    }
                    const vName = $0.message && $0.message.model && typeof $0.message.model.variant === 'string'
                        ? $0.message.model.variant.trim()
                        : '';
                    if (vName) {
                        if (!$0.model.variants || typeof $0.model.variants !== 'object') {
                            $0.model.variants = {};
                        }
                        if (!$0.model.variants[vName] || typeof $0.model.variants[vName] !== 'object') {
                            $0.model.variants[vName] = {};
                        }
                        $0.model.variants[vName].temperature = $2;
                    }
                }
            """

    /// Which execution this observation belongs to, if any. Managed-ness is
    /// durable evidence: an exact `Accepted` execution for the very physical
    /// message this request carries. A request the plugin never admitted (Host
    /// compaction, title generation, an auxiliary child) has no such execution
    /// and is left entirely to the Host.
    let private tryExecutionKey (input: obj) =
        match trySessionId input, tryPhysicalUserMessageId input with
        | Some sessionId, Some physical ->
            Some
                { SessionId = sessionId
                  PhysicalUserMessageId = physical }
        | _ -> None

    /// context-compression [001]: the context limit is the provider's own
    /// declaration for this model, read off the resolved model opencode hands
    /// over here — not a size derived from our own measurements. It is
    /// disclosed to the model as one datum about its state; nothing predicts
    /// overflow from it.
    let private declaredContextLimit (input: obj) : int option =
        let model = if isNull input then null else input?model
        let limit = if isNull model then null else model?limit
        let context = if isNull limit then null else limit?context

        if emitJsExpr context "typeof $0 === 'number' && Number.isFinite($0) && $0 > 0" then
            Some(int (unbox<float> context))
        else
            None

    let private recordDeclaredLimit (input: obj) =
        match trySessionId input, declaredContextLimit input with
        | Some sessionId, Some limit -> calibration?recordLimit (SessionId.value sessionId, limit) |> ignore
        | _ -> ()

    /// Managed-ness is durable evidence: only a message this plugin durably
    /// accepted is a managed provider run. Such a run must hold the exact
    /// committed lease for its own physical message, or the observation fails
    /// closed. A message nobody accepted is entirely the Host's — never rejected
    /// for a binding this hook does not own.
    let private validateManagedObservation (journal: AgentJournal option) (agent: string) (key: ChatExecutionKey) =
        match readAdmission key with
        | Some _ -> ()
        | None when ModelRouting.wasStoppedExecutionSuperseded key -> ()
        | None when not (SessionExecutionBinding.isManagedExecution journal key) -> ()
        | None ->
            invalidOp (
                sprintf
                    "PROMPT-006: managed provider run '%s' for session '%s' has no committed execution lease for physical user message '%s'"
                    agent
                    (SessionId.value key.SessionId)
                    (PhysicalUserMessageId.value key.PhysicalUserMessageId)
            )

    /// The observation barrier's whole policy: read the exact committed lease,
    /// fail closed only when a managed run has none, and project only the
    /// approved temperature. A message nobody durably accepted is left
    /// untouched.
    let private projectManagedTemperature (input: obj) (output: obj) =
        if supportsTemperature input then
            applyManagedTemperature input output

    let private isExplicitlyUnmanagedAgent input =
        match tryAgent input with
        | Some agent when not (ManagedAgent.isManagedName agent) -> true
        | _ -> false

    let private applyManagedPolicy (journal: AgentJournal option) input output =
        match isExplicitlyUnmanagedAgent input, tryAgent input, tryExecutionKey input with
        | false, Some agent, Some key when
            readAdmission key |> Option.isSome
            || ModelRouting.wasStoppedExecutionSuperseded key
            ->
            validateManagedObservation journal agent key
            projectManagedTemperature input output
        | false, Some agent, Some key -> validateManagedObservation journal agent key
        | _ -> ()

    let private handleInput (journal: AgentJournal option) (input: obj) (output: obj) =
        recordDeclaredLimit input
        applyManagedPolicy journal input output

    let private handleVerdict (verdict: ParamsVerdict) =
        match verdict with
        | ParamsVerdict.Accept -> ()
        | ParamsVerdict.Adjust _ -> ()
        | ParamsVerdict.Refuse reason -> invalidOp reason

    let private executeTurnParams (turn: ITurnTransform) (key: ChatExecutionKey) (input: obj) =
        let turnContext: TurnContext =
            { Session = key.SessionId
              Role = tryAgent input |> Option.map AgentRole
              Route = None
              Workspace = None }

        let providerParams: ProviderParams =
            { Temperature = None
              TopP = None
              MaxOutputTokens = declaredContextLimit input
              Reasoning = None
              Extra = None }

        let verdict =
            (turn.ProviderParams(turnContext, providerParams)).GetAwaiter().GetResult()

        handleVerdict verdict

    let private applyTurnParams (turnOpt: ITurnTransform option) (input: obj) (_output: obj) =
        match turnOpt, tryExecutionKey input with
        | Some turn, Some key -> executeTurnParams turn key input
        | _ -> ()

    let private handleInputWithTurn
        (turnOpt: ITurnTransform option)
        (journal: AgentJournal option)
        (input: obj)
        (output: obj)
        =
        if not (isExplicitlyUnmanagedAgent input) then
            applyTurnParams turnOpt input output
        handleInput journal input output

    /// Host invokes chat.params as a native two-argument JS function. The boxed
    /// F# lambda is emitted as a two-arity arrow, so re-expose it through an Emit
    /// wrapper invoked with both arguments (no curried second stage exists).
    [<Emit("(input, output) => $0(input, output)")>]
    let private asBinaryJsFunction (fn: obj) : obj = jsNative

    let createWithTurn (journal: AgentJournal option) (turnOpt: ITurnTransform option) : obj =
        asBinaryJsFunction (box (fun (input: obj) (output: obj) -> handleInputWithTurn turnOpt journal input output))

    let createWith (journal: AgentJournal option) : obj = createWithTurn journal None

    let create () : obj = createWith None
