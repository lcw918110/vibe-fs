namespace Wanxiangshu.OpenCode

open System.Threading.Tasks
open Wanxiangshu.Foundation
open Wanxiangshu.Foundation.Identity
open Wanxiangshu.Host.Ports
open Wanxiangshu.Participant.Provider

module ProviderSystemTransform =
    /// planStage resolves the current incumbency's stage-method semantic path
    /// ("planning/s1" | "planning/s2" | "planning/s3") plus the derived draft
    /// path (substituted as `{{draft_path}}` in the stage guidance) for Plan
    /// sessions, and None otherwise (work-031 stage guidance).
    val createWith:
        role: (SessionId -> Role option) ->
        planStage: (SessionId -> Task<(string * string) option>) ->
            (obj -> obj -> Task<unit>)

    /// HNI P-06: provider system transform integrated with ITurnTransform.
    val createWithTurn:
        role: (SessionId -> Role option) ->
        planStage: (SessionId -> Task<(string * string) option>) ->
        turnOpt: ITurnTransform option ->
            (obj -> obj -> Task<unit>)
