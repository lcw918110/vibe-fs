namespace Wanxiangshu.OpenCode

open System
open System.Threading.Tasks
open Wanxiangshu.Context.Prefix
open Wanxiangshu.Context.Trace
open Wanxiangshu.Mission.Work.OpenCode
open Wanxiangshu.Mission.Work

module PluginTransforms =

    /// The outgoing parent provider-wire prefix; temp sessions only append an ephemeral question.
    val negotiationIsTemporary: sessionId: string -> bool
    val negotiationToolViolationCount: sessionId: string -> int
    val negotiationRecordToolViolation: sessionId: string -> unit
    val negotiationRememberSystem: input: obj -> output: obj -> unit
    val negotiationRememberModel: input: obj -> unit
    val negotiationModelFor: sessionId: string -> obj
    val negotiationStartTemporary: tempId: string -> delegatorId: string -> Result<unit, string>
    val consultationStartPrevious: tempId: string -> sourceSessionId: string -> metadata: obj -> Result<unit, string>
    val consultationModelForTemporary: tempId: string -> obj
    val consultationModelForPrevious: sourceSessionId: string -> metadata: obj -> obj
    val negotiationFinishTemporary: tempId: string -> unit
    val negotiationForgetSession: sessionId: string -> unit
    val negotiationProjectSystem: input: obj -> output: obj -> bool
    val negotiationRememberMessages: sessionId: string -> outObj: obj -> unit
    val negotiationProjectMessages: sessionId: string -> outObj: obj -> bool

    type TraceTransformCapture =
        { RawMessages: obj list
          Current: XTraceProjectionState option
          Receipt: XTraceCaptureReceipt option }

    /// 交付裁决（条款 4）：Continue = 仍需继续；Stop = 无需继续，本次请求在
    /// transform 钩子处被中断。
    type DeliveryAttemptFence =
        { SessionId: Wanxiangshu.Foundation.Identity.SessionId
          ProviderRun: Wanxiangshu.Foundation.Identity.ProviderRunIdentity
          CallId: Wanxiangshu.Foundation.Identity.ToolCallId }

    [<RequireQualifiedAccess>]
    type DeliveryAdjudication =
        | ContinueDelivery
        | StopDelivery of DeliveryAttemptFence

    type NormalTransformCapabilities =
        { BeginPhysicalProviderAttempt: string option -> obj -> Task<unit>
          StopCompletedContinuation:
              string option -> Wanxiangshu.Foundation.Identity.PhysicalUserMessageId option -> obj -> Task<bool>
          BindSessionStartedAt: string option -> Task<DateTimeOffset option>
          ApplyRelayProjection: string option -> obj -> Task<RelayProjectionDisposition>
          ApplyTenureIsolation: string option -> obj -> Task<unit>
          CaptureXTraceMessages: string option -> obj -> Task<TraceTransformCapture>
          RefreshCompanionXTrace: string option -> XTraceProjectionState option -> unit
          ApplyCompanion: string option -> obj -> obj -> Task<unit>
          ApplyXWire: obj -> Task
          FreezeProviderAttemptPlan:
              string option -> Wanxiangshu.Foundation.Identity.PhysicalUserMessageId option -> Task<unit>
          InjectPairGuideline: string option -> DateTimeOffset option -> obj -> Task<unit>
          SettleAndReplaceDeferredInspections: string option -> obj -> Task<unit>
          SanitizeMessages: string option -> obj -> unit
          AdjudicateDelivery: string option -> obj -> Task<DeliveryAdjudication>
          StopAfterDelivery:
              string option
                  -> Wanxiangshu.Foundation.Identity.PhysicalUserMessageId option
                  -> DeliveryAttemptFence
                  -> obj
                  -> Task<unit> }

    type TransformBranchCapabilities = { RegisterOwned: string -> unit }

    val normalTransform:
        caps: NormalTransformCapabilities ->
        projectionSessionIdOpt: string option ->
        inObj: obj ->
        outObj: obj ->
            Task<unit>

    /// Provider-facing transform composition: order only.
    /// Relay cut → Companion/XWire → pair; retired raw history is removed
    /// before any downstream context owner.
    val create: boot: PluginBoot.Boot -> host: PluginHostWiring.Host -> (obj -> obj -> Task<unit>)
