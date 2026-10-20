namespace Wanxiangshu.OpenCode

open System
open System.Threading.Tasks
open Wanxiangshu.Context.Prefix
open Wanxiangshu.Context.Trace
open Wanxiangshu.Mission.Work.OpenCode

module PluginTransforms =

    type TraceTransformCapture =
        { RawMessages: obj list
          Current: XTraceProjectionState option
          Receipt: XTraceCaptureReceipt option }

    type NormalTransformCapabilities =
        { BeginPhysicalProviderAttempt: string option -> obj -> Task<unit>
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
          SanitizeMessages: obj -> unit }

    type TransformBranchCapabilities = { RegisterOwned: string -> unit }

    val defaultCapabilities: boot: PluginBoot.Boot -> host: PluginHostWiring.Host -> NormalTransformCapabilities

    val defaultBranchCapabilities: boot: PluginBoot.Boot -> host: PluginHostWiring.Host -> TransformBranchCapabilities

    val normalTransform:
        caps: NormalTransformCapabilities ->
        projectionSessionIdOpt: string option ->
        inObj: obj ->
        outObj: obj ->
            Task<unit>

    val createWithCaps:
        caps: NormalTransformCapabilities -> branches: TransformBranchCapabilities -> (obj -> obj -> Task<unit>)

    /// Provider-facing transform composition: order only.
    /// Relay cut → Companion/XWire → pair; retired raw history is removed
    /// before any downstream context owner.
    val create: boot: PluginBoot.Boot -> host: PluginHostWiring.Host -> (obj -> obj -> Task<unit>)
