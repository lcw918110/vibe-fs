namespace Wanxiangshu.Execution.Delegation.Fork.OpenCode

open Wanxiangshu.OpenCode

/// Manager fork & resume / Orchestrator commission. One typed request backs
/// every public tool; each tool exposes its own schema; PTY is absent.
module ForkTool =

    [<RequireQualifiedAccess>]
    module Path =
        [<RequireQualifiedAccess>]
        module Fork =
            [<Literal>]
            val Description: string = "tool/fork/description"

            [<Literal>]
            val ArgCalling: string = "tool/fork/arg-calling"

            [<Literal>]
            val ArgName: string = "tool/fork/arg-name"

            [<Literal>]
            val ArgCharge: string = "tool/fork/arg-charge"

            [<Literal>]
            val ArgKeywords: string = "tool/fork/arg-keywords"

            [<Literal>]
            val ArgAttach: string = "delegation/fork-attach-argument"

            [<Literal>]
            val AttachUnknown: string = "delegation/fork-attach-unknown"

            [<Literal>]
            val AttachSelf: string = "delegation/fork-attach-self"

            [<Literal>]
            val NameRequired: string = "tool/fork/name-required"

            [<Literal>]
            val ChargeRequired: string = "tool/fork/charge-required"

            [<Literal>]
            val CallingConflict: string = "tool/fork/calling-conflict"

            [<Literal>]
            val UnknownCalling: string = "tool/fork/unknown-calling"

            [<Literal>]
            val ChargeContextUnavailable: string = "tool/fork/charge-context-unavailable"

            [<Literal>]
            val NameAlreadyBelongs: string = "tool/fork/name-already-belongs"

            [<Literal>]
            val WarmStartUnavailable: string = "tool/fork/warm-start-unavailable"

            [<Literal>]
            val ChargeCarried: string = "tool/fork/charge-carried"

            [<Literal>]
            val ChargeNotPlaced: string = "tool/fork/charge-not-placed"

            [<Literal>]
            val ChargePlacementUncertain: string = "tool/fork/charge-placement-uncertain"

            [<Literal>]
            val PersonUnknown: string = "tool/fork/person-unknown"

            [<Literal>]
            val PersonUnavailable: string = "tool/fork/person-unavailable"

            [<Literal>]
            val PersonCannotTakeCharge: string = "tool/fork/person-cannot-take-charge"

            [<Literal>]
            val HandoffJournalRequired: string = "tool/fork/handoff-journal-required"

            [<Literal>]
            val PersonSessionUnknown: string = "tool/fork/person-session-unknown"

            [<Literal>]
            val HandoffAppendFailed: string = "tool/fork/handoff-append-failed"

        [<RequireQualifiedAccess>]
        module Commission =
            [<Literal>]
            val Description: string = "tool/commission/description"

            [<Literal>]
            val ArgCalling: string = "tool/commission/arg-calling"

            [<Literal>]
            val ArgName: string = "tool/commission/arg-name"

            [<Literal>]
            val ArgCharge: string = "tool/commission/arg-charge"

            [<Literal>]
            val AuthorityRequired: string = "tool/commission/authority-required"

            [<Literal>]
            val NameRequired: string = "tool/commission/name-required"

            [<Literal>]
            val ChargeRequired: string = "tool/commission/charge-required"

            [<Literal>]
            val UnknownCalling: string = "tool/commission/unknown-calling"

            [<Literal>]
            val NameAlreadyBelongs: string = "tool/commission/name-already-belongs"

            [<Literal>]
            val ChargeTaken: string = "tool/commission/charge-taken"

            [<Literal>]
            val RoadNotOpened: string = "tool/commission/road-not-opened"

            [<Literal>]
            val RoadCannotTakeCharge: string = "tool/commission/road-cannot-take-charge"

        [<RequireQualifiedAccess>]
        module Resume =
            [<Literal>]
            val Description: string = "tool/resume/description"

            [<Literal>]
            val CallingNotAllowed: string = "tool/resume/calling-not-allowed"

            [<Literal>]
            val GuidanceSent: string = "tool/resume/guidance-sent"

            [<Literal>]
            val AssessmentPendingForDevOps: string = "tool/resume/assessment-pending-for-devops"

    type Request =
        { Calling: string
          Name: string
          Charge: string
          Keywords: string
          Attach: string option
          ExpectedToolCalls: int option }

    val managerAdmission: ToolAdmission
    val resumeAdmission: ToolAdmission
    val orchestratorAdmission: ToolAdmission
    val managerSpec: factory: HostToolFactory -> scope: ToolRuntimeScope -> ToolSpec
    val resumeSpec: factory: HostToolFactory -> scope: ToolRuntimeScope -> ToolSpec
    val orchestratorSpec: factory: HostToolFactory -> scope: ToolRuntimeScope -> ToolSpec
