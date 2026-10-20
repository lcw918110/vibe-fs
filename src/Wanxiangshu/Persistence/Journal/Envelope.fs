namespace Wanxiangshu.Persistence.Journal

open Wanxiangshu.Composition.Durable

open System
open Fable.Core
open Fable.Core.JsInterop
open Thoth.Json
open Wanxiangshu.Change
open Wanxiangshu.Context.Companion
open Wanxiangshu.Execution.Delegation
open Wanxiangshu.Execution.Fission
open Wanxiangshu.Execution.Session.ChatExecution
open Wanxiangshu.Foundation
open Wanxiangshu.Foundation.Identity
open Wanxiangshu.Host
open Wanxiangshu.Composition.Durable.Fact
open Wanxiangshu.Interaction.Authority
open Wanxiangshu.Participant.Provider.Attempt.Fallback

type StreamId =
    | Workspace
    | Session of SessionId
    | Child of ChildId
    | Process of ProcessId

/// One durable journal line (PERSIST-001).
/// DSL-state-combination: domain — stream and optional provider-run identity
/// route one durable fact envelope; absence means the fact has no run, not a
/// workflow stage.
type Envelope =
    {
        RuntimeId: RuntimeId
        LocalSeq: LocalSeq
        ObservedAt: ObservedAt
        EventId: EventId
        Stream: StreamId
        /// The provider run this fact was observed during, when there was one.
        ///
        /// Replaces the previous `TurnId`, which was a third name for the same
        /// thing: HOST-010 establishes that one assistant message is one provider
        /// request is one turn, so a separate turn identity could only ever be a
        /// copy of the run id — or disagree with it.
        ///
        /// `None` for facts that belong to no run: runtime start, worktree
        /// creation, a Manager job's lifecycle.
        ProviderRun: ProviderRunIdentity option
        Fact: Fact
    }

module Envelope =

    let private baseExtra =
        { Extra.empty with
            Hash = "system-int64"
            Coders =
                Extra.empty.Coders
                |> Map.add "System.Int64" (Encode.boxEncoder Encode.int64, Decode.boxDecoder Decode.int64) }

    let private extra = PromptFactCodec.withCoder baseExtra

    let private compareAcrossRuntimes (a: Envelope) (b: Envelope) : int =
        let byObservation = compare a.ObservedAt b.ObservedAt

        if byObservation <> 0 then
            byObservation
        else
            String.Compare(RuntimeId.value a.RuntimeId, RuntimeId.value b.RuntimeId, StringComparison.Ordinal)

    /// PERSIST-001 ordering: within a runtime by LocalSeq, across runtimes by
    /// observation time with the runtime id as the tie-break.
    ///
    /// The tie-break is not cosmetic. Two runtimes can observe facts in the same
    /// millisecond, and a fold must be deterministic across restarts, so the
    /// order cannot depend on which line the reader happened to see first.
    let compareSortKey (a: Envelope) (b: Envelope) : int =
        if a.RuntimeId = b.RuntimeId then
            compare (LocalSeq.value a.LocalSeq) (LocalSeq.value b.LocalSeq)
        else
            compareAcrossRuntimes a b

    /// PERSIST-001: the line is the durable artifact, so its bytes must not depend
    /// on the machine that wrote it.
    ///
    /// `ObservedAt` is pinned to offset zero before encoding. Writers always pass
    /// `DateTimeOffset.UtcNow`, so this changes nothing they produce — but the
    /// DECODER attaches the reader's local offset, so without this a line read on a
    /// `TZ=Asia/Shanghai` host and written back would render `+08:00` for the same
    /// instant. Two hosts would then disagree on the bytes of one history, and a
    /// byte comparison of two replicas would report a difference that is not one.
    ///
    /// `ToOffset TimeSpan.Zero` rather than `ToUniversalTime()`: Fable's
    /// `toUniversalTime` leaves the emitted value's `offset` field `undefined`, and
    /// the encoder then renders a bare `Z` by accident rather than by contract.
    let private streamEncoder (stream: StreamId) : JsonValue =
        match stream with
        | Workspace -> Encode.object [ "Workspace", Encode.nil ]
        | Session id -> Encode.object [ "Session", Encode.string (SessionId.value id) ]
        | Child id -> Encode.object [ "Child", Encode.string (ChildId.value id) ]
        | Process id -> Encode.object [ "Process", Encode.string (ProcessId.value id) ]

    let private providerRunEncoder (run: ProviderRunIdentity option) : JsonValue =
        match run with
        | None -> Encode.nil
        | Some id -> Encode.string (ProviderRunIdentity.value id)

    let serialize (envelope: Envelope) : string =
        match envelope.Fact with
        | _ ->
            Encode.Auto.toString (
                0,
                { envelope with
                    ObservedAt = envelope.ObservedAt.ToOffset TimeSpan.Zero },
                extra = extra
            )

    /// PERSIST-005: a pre-0.5.0 / pre-tip-v2 line is refused, never guessed into shape.
    /// Tip v2 must be checked here (Boot reads envelopes) not only in FactCodec.deserializeFact:
    /// Auto-decode of BlogObservationCommitted without TipRuleId yields an opaque Thoth error,
    /// Boot truncates the stream mid-file, later Abandon/Commit vanish, and fold then dies on
    /// "already has open request" — a lie about the real cause. Pre-cutover observation tags
    /// are not decoded.
    let private streamDecoder: Decoder<StreamId> =
        Decode.object (fun get ->
            if get.Optional.Field "Workspace" Decode.unit |> Option.isSome then
                StreamId.Workspace
            elif get.Optional.Field "Session" Decode.string |> Option.isSome then
                StreamId.Session(SessionId.create (get.Required.Field "Session" Decode.string))
            elif get.Optional.Field "Child" Decode.string |> Option.isSome then
                StreamId.Child(ChildId.create (get.Required.Field "Child" Decode.string))
            else
                StreamId.Process(ProcessId.create (get.Required.Field "Process" Decode.string)))

    let private providerRunDecoder: Decoder<ProviderRunIdentity option> =
        Decode.option (Decode.string |> Decode.map ProviderRunIdentity.create)

    let private taggedStringDecoder expected create : Decoder<'a> =
        Decode.index 0 Decode.string
        |> Decode.andThen (fun actual ->
            if actual = expected then
                Decode.index 1 Decode.string |> Decode.map create
            else
                Decode.fail (sprintf "expected %s, got %s" expected actual))

    let private sessionIdDecoder = taggedStringDecoder "SessionId" SessionId.create
    let private blobRefDecoder = taggedStringDecoder "BlobRef" BlobRef.create
    let private blobDigestDecoder = taggedStringDecoder "BlobDigest" BlobDigest.create

    let private providerRunIdentityDecoder =
        taggedStringDecoder "ProviderRunIdentity" ProviderRunIdentity.create

    let private toolCallIdDecoder = taggedStringDecoder "ToolCallId" ToolCallId.create

    let private hostToolPartIdDecoder =
        taggedStringDecoder "HostToolPartId" HostToolPartId.create

    let private xTracePartAppendedPayloadDecoder =
        Decode.object (fun get ->
            {| SessionId = get.Required.Field "SessionId" sessionIdDecoder
               CursorSequence = get.Required.Field "CursorSequence" Decode.int64
               Role = get.Required.Field "Role" Decode.string
               Turn = get.Required.Field "Turn" Decode.int
               PartIndex = get.Required.Field "PartIndex" Decode.int
               Kind = get.Required.Field "Kind" Decode.string
               ToolName = get.Optional.Field "ToolName" Decode.string
               TextRef = get.Required.Field "TextRef" blobRefDecoder
               TextDigest = get.Required.Field "TextDigest" blobDigestDecoder
               Provenance = get.Required.Field "Provenance" Decode.string
               ProviderRun = get.Optional.Field "ProviderRun" providerRunIdentityDecoder
               ToolCallId = get.Optional.Field "ToolCallId" toolCallIdDecoder
               HostToolPartId = get.Optional.Field "HostToolPartId" hostToolPartIdDecoder |})

    let private xTracePartAppendedDecoder: Decoder<CompanionFactCases> =
        Decode.index 1 xTracePartAppendedPayloadDecoder
        |> Decode.map CompanionFactCases.XTracePartAppended

    let private runtimeFactDecoder =
        Decode.Auto.generateDecoderCached<RuntimeFact> (extra = extra)

    let private promptFactDecoder =
        Decode.Auto.generateDecoderCached<PromptFactCases> (extra = extra)

    [<RequireQualifiedAccess>]
    type private LegacyFallbackDto =
        | FallbackCursorAdvanced of
            {| SessionId: SessionId
               LogicalRunId: LogicalRunId
               AuthorityRootUserMessageId: AuthorityRootUserMessageId
               ProviderRun: ProviderRunIdentity
               PreviousOffset: byte
               NextOffset: byte
               ConsecutiveFailureCount: int
               Reason: string |}
        | FallbackExhausted of
            {| SessionId: SessionId
               LogicalRunId: LogicalRunId
               AuthorityRootUserMessageId: AuthorityRootUserMessageId
               FinalConsecutiveFailureCount: int
               FinalOffset: byte |}
        | FallbackSucceeded of
            {| SessionId: SessionId
               LogicalRunId: LogicalRunId
               AuthorityRootUserMessageId: AuthorityRootUserMessageId
               ProviderRun: ProviderRunIdentity |}

    let private providerFailureFactDecoder =
        Decode.Auto.generateDecoderCached<ProviderFailureFactCases> (extra = extra)

    let private legacyFallbackDecoder: Decoder<ProviderFailureFactCases> =
        Decode.Auto.generateDecoderCached<LegacyFallbackDto> (extra = extra)
        |> Decode.map (function
            | LegacyFallbackDto.FallbackCursorAdvanced payload ->
                ProviderFailureFactCases.FailureRecorded
                    {| SessionId = payload.SessionId
                       LogicalRunId = payload.LogicalRunId
                       AuthorityRootUserMessageId = payload.AuthorityRootUserMessageId
                       ProviderRun = payload.ProviderRun
                       ConsecutiveFailureCount = payload.ConsecutiveFailureCount
                       Reason = payload.Reason |}
            | LegacyFallbackDto.FallbackExhausted payload ->
                ProviderFailureFactCases.RetryExhausted
                    {| SessionId = payload.SessionId
                       LogicalRunId = payload.LogicalRunId
                       AuthorityRootUserMessageId = payload.AuthorityRootUserMessageId
                       FinalConsecutiveFailureCount = payload.FinalConsecutiveFailureCount |}
            | LegacyFallbackDto.FallbackSucceeded payload ->
                ProviderFailureFactCases.SuccessRecorded
                    {| SessionId = payload.SessionId
                       LogicalRunId = payload.LogicalRunId
                       AuthorityRootUserMessageId = payload.AuthorityRootUserMessageId
                       ProviderRun = payload.ProviderRun |})

    let private executionFactDecoder =
        Decode.Auto.generateDecoderCached<ExecutionFactCases> (extra = extra)

    let private orchestratorFactDecoder =
        Decode.Auto.generateDecoderCached<OrchestratorFactCases> (extra = extra)

    let private genericCompanionFactDecoder =
        Decode.Auto.generateDecoderCached<CompanionFactCases> (extra = extra)

    let private companionFactDecoder: Decoder<CompanionFactCases> =
        Decode.index 0 Decode.string
        |> Decode.andThen (function
            | "XTracePartAppended" -> xTracePartAppendedDecoder
            | _ -> genericCompanionFactDecoder)

    /// K2: BlogObservationCommitted 手写分流。新字节以 TipName 为 canonical
    /// 键；tip v2 时代旧字节以 TipRuleId 兜底（两者皆缺 → 拒绝，与 tip v2
    /// clean break 语义一致）。FieldNameAtCommit 不读；TipTextRef /
    let private int64TaggedDecoder expected create : Decoder<'a> =
        Decode.index 0 Decode.string
        |> Decode.andThen (fun actual ->
            if actual = expected then
                Decode.index 1 Decode.int64 |> Decode.map create
            else
                Decode.fail (sprintf "expected %s, got %s" expected actual))

    let private frameEpochIdDecoder =
        int64TaggedDecoder "FrameEpochId" FrameEpochId.create

    let private prefixEpochIdDecoder =
        int64TaggedDecoder "PrefixEpochId" PrefixEpochId.create

    let private contextFactDecoder: Decoder<ContextFactCases> =
        Decode.Auto.generateDecoderCached<ContextFactCases> (extra = extra)

    let private hostFactDecoder: Decoder<HostFactCases> =
        Decode.index 0 Decode.string
        |> Decode.andThen (fun _ -> Decode.Auto.generateDecoderCached<HostFactCases> (extra = extra))

    let private fissionFactDecoder =
        Decode.Auto.generateDecoderCached<FissionFactCases> (extra = extra)

    let private delegationFactDecoder =
        Decode.Auto.generateDecoderCached<DelegationFactCases> (extra = extra)

    let private chatExecutionFactDecoder =
        Decode.Auto.generateDecoderCached<ChatExecutionFactCases> (extra = extra)

    let private cognitionFactDecoder =
        Decode.Auto.generateDecoderCached<LegacyCognitionFact> (extra = extra)

    let private familyCase decoder wrap =
        Decode.index 1 decoder |> Decode.map wrap

    let private agentFactDecoder: Decoder<AgentFact> =
        Decode.index 0 Decode.string
        |> Decode.andThen (function
            | "Prompt" -> familyCase promptFactDecoder AgentFact.Prompt
            | "ProviderFailure" -> familyCase providerFailureFactDecoder AgentFact.ProviderFailure
            | "Fallback" -> familyCase legacyFallbackDecoder AgentFact.ProviderFailure
            | "Execution" -> familyCase executionFactDecoder AgentFact.Execution
            | "Orchestrator" -> familyCase orchestratorFactDecoder AgentFact.Orchestrator
            | "Companion" -> familyCase companionFactDecoder AgentFact.Companion
            | "Context" -> familyCase contextFactDecoder AgentFact.Context
            | "Host" -> familyCase hostFactDecoder AgentFact.Host
            | "Fission" -> familyCase fissionFactDecoder AgentFact.Fission
            | "Delegation" -> familyCase delegationFactDecoder AgentFact.Delegation
            | "ChatExecution" -> familyCase chatExecutionFactDecoder AgentFact.ChatExecution
            | "Cognition" -> familyCase cognitionFactDecoder AgentFact.Cognition
            | name -> Decode.fail ("Cannot find AgentFact case " + name))

    let private factDecoder: Decoder<Fact> =
        Decode.index 0 Decode.string
        |> Decode.andThen (function
            | "Runtime" -> Decode.index 1 runtimeFactDecoder |> Decode.map Fact.Runtime
            | "Agent" -> Decode.index 1 agentFactDecoder |> Decode.map Fact.Agent
            | name -> Decode.fail ("Cannot find Fact case " + name))

    let private decodeExtra =
        Extra.withCustom (fun (_: Fact) -> Encode.nil) factDecoder extra

    let private currentEnvelopeDecoder: Decoder<Envelope> =
        Decode.Auto.generateDecoderCached<Envelope> (extra = decodeExtra)

    let private deserializeCurrentEnvelope json =
        Decode.fromString currentEnvelopeDecoder json
        |> Result.bind (fun envelope -> FactCodec.validateFact envelope.Fact |> Result.map (fun _ -> envelope))

    let private deserializeCurrentEnvelopeValue (value: JsonValue) =
        Decode.fromValue "$" currentEnvelopeDecoder value
        |> Result.bind (fun envelope -> FactCodec.validateFact envelope.Fact |> Result.map (fun _ -> envelope))

    let deserialize (json: string) : Result<Envelope, string> =
        if FactCodec.containsLegacyFallbackFields json then
            Error FactCodec.pre050MigrationMessage
        elif FactCodec.containsLegacyScoreVectorEntry json then
            Error FactCodec.tipV2CleanBreakMessage
        else
            deserializeCurrentEnvelope json

    /// EventStore already owns a parsed canonical payload. Decode it directly
    /// instead of stringify -> parse on every replayed Journal event.
    let deserializeValue (value: JsonValue) : Result<Envelope, string> = deserializeCurrentEnvelopeValue value
