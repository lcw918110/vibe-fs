namespace Wanxiangshu.OpenCode

open System.Threading.Tasks
open Wanxiangshu.Mission.Work.OpenCode

module PluginTransformSurface =
    let ordinaryEffects () : Task<string array> =
        task {
            let effects = ResizeArray<string>()
            let record name = effects.Add name

            let complete name =
                record name
                Task.FromResult()

            let caps: PluginTransforms.NormalTransformCapabilities =
                { BeginPhysicalProviderAttempt = fun _ _ -> complete "begin"
                  BindSessionStartedAt =
                    fun _ ->
                        record "session-time"
                        Task.FromResult None
                  ApplyRelayProjection =
                    fun _ _ ->
                        record "relay"
                        Task.FromResult RelayProjectionDisposition.CurrentTenure
                  ApplyTenureIsolation = fun _ _ -> complete "tenure-isolation"
                  CaptureXTraceMessages =
                    fun _ _ ->
                        record "capture"

                        Task.FromResult
                            { RawMessages = []
                              Current = None
                              Receipt = None }
                  RefreshCompanionXTrace = fun _ _ -> record "refresh-companion"
                  ApplyCompanion = fun _ _ _ -> complete "companion"
                  ApplyXWire =
                    fun _ ->
                        record "prefix"
                        Task.FromResult()
                  FreezeProviderAttemptPlan = fun _ _ -> complete "freeze-plan"
                  InjectPairGuideline = fun _ _ _ -> complete "pair"
                  SettleAndReplaceDeferredInspections = fun _ _ -> complete "deferred"
                  SanitizeMessages = fun _ -> record "sanitize" }

            do!
                PluginTransforms.normalTransform
                    caps
                    (Some "surface-owner")
                    (box {| sessionID = "surface-owner" |})
                    (box {| messages = ([||]: obj array) |})

            return effects.ToArray()
        }
