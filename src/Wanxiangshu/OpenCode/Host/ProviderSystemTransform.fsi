namespace Wanxiangshu.OpenCode

open System.Threading.Tasks
open Wanxiangshu.Foundation
open Wanxiangshu.Foundation.Identity

/// HOST-026 / PROMPT-017: project the session-bound ProviderLanguage onto the
/// Wanxiangshu-owned system-prompt segment without disturbing Host/AGENTS text.
open Wanxiangshu.Participant.Provider

module ProviderSystemTransform =
    /// planStage resolves the current incumbency's stage-method semantic path
    /// ("planning/s1" | "planning/s2" | "planning/s3") for Plan sessions and
    /// None otherwise (work-031 stage guidance).
    val createWith:
        role: (SessionId -> Role option) ->
        planStage: (SessionId -> Task<string option>) ->

            (obj -> obj -> Task<unit>)
