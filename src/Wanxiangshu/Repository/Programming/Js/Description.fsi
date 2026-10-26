namespace Wanxiangshu.Repository.Programming.Js

type JsExample =
    { Requires: Set<JsCapability>
      Source: string }

module JsCanonicalDescription =
    [<RequireQualifiedAccess>]
    module Path =
        [<Literal>]
        val Header: string = "tool/js-program/header"

        [<Literal>]
        val Footer: string = "tool/js-program/footer"

        [<Literal>]
        val Contract: string = "tool/js-program/contract"

        [<Literal>]
        val ContractParallelEdits: string = "tool/js-program/contract-parallel-edits"

        [<Literal>]
        val ContractParallelReads: string = "tool/js-program/contract-parallel-reads"

        [<Literal>]
        val VerbRead: string = "tool/js-program/verb-read"

        [<Literal>]
        val VerbCreate: string = "tool/js-program/verb-create"

        [<Literal>]
        val ReadRules: string = "tool/js-program/rules-read"

        [<Literal>]
        val ReviewOnlyRules: string = "tool/js-program/review-only"

        [<Literal>]
        val Recipes: string = "tool/js-program/recipes"

        [<Literal>]
        val WriteRules: string = "tool/js-program/rules-write"

        [<Literal>]
        val MutationRules: string = "tool/js-program/rules-mutation"

        [<Literal>]
        val Rm: string = "tool/js-program/rm"

        [<Literal>]
        val Mv: string = "tool/js-program/mv"

        [<Literal>]
        val RunRules: string = "tool/js-program/run-rules"

        [<Literal>]
        val ViewDeclarationExtra: string = "tool/js-program/view-declaration-extra"

        [<Literal>]
        val Deliver: string = "tool/js-program/deliver"

        [<Literal>]
        val DelegationRules: string = "tool/js-program/delegation-rules"

        [<Literal>]
        val UltraFraming: string = "tool/js-program/ultra-framing"

        [<Literal>]
        val UltraUnavailable: string = "tool/js-program/ultra-unavailable"

        [<Literal>]
        val UltraEngineering: string = "tool/js-program/ultra-engineering"

        [<Literal>]
        val UltraInvestigation: string = "tool/js-program/ultra-investigation"

        [<Literal>]
        val UltraDevOps: string = "tool/js-program/ultra-devops"

        [<Literal>]
        val UltraManagerInvestigation: string = "tool/js-program/ultra-manager-investigation"

        [<Literal>]
        val UltraPlan: string = "tool/js-program/ultra-plan"

        [<Literal>]
        val MechanicalSemantic: string = "tool/js-program/mechanical-semantic"

        [<Literal>]
        val ReasonRunUnimplemented: string = "tool/js-program/reason-run-unimplemented"

        [<Literal>]
        val ArgProgram: string = "tool/js-program/arg-program"

        [<Literal>]
        val MissingProgram: string = "tool/js-program/missing-program"

        [<Literal>]
        val HookNotVisible: string = "tool/js-program/hook-not-visible"

        [<Literal>]
        val Park: string = "tool/js-program/park"

        [<Literal>]
        val ParkedPayload: string = "tool/js-program/parked-payload"

        [<Literal>]
        val BaseClass: string = "tool/js-program/base-class"

        [<Literal>]

        [<Literal>]
        val BaseClassRead: string = "tool/js-program/base-class-read"

        [<Literal>]
        val BaseClassAnchors: string = "tool/js-program/base-class-anchors"

        [<Literal>]
        val BaseClassGlob: string = "tool/js-program/base-class-glob"

        [<Literal>]
        val BaseClassGrep: string = "tool/js-program/base-class-grep"

        [<Literal>]
        val BaseClassWrite: string = "tool/js-program/base-class-write"

        [<Literal>]
        val BaseClassRemove: string = "tool/js-program/base-class-remove"

        [<Literal>]
        val BaseClassMove: string = "tool/js-program/base-class-move"

        [<Literal>]
        val BaseClassExec: string = "tool/js-program/base-class-exec"

        [<Literal>]
        val BaseClassFork: string = "tool/js-program/base-class-fork"

        [<Literal>]
        val BaseClassResume: string = "tool/js-program/base-class-resume"

        [<Literal>]
        val BaseClassCommission: string = "tool/js-program/base-class-commission"

        [<Literal>]
        val BaseClassDevOps: string = "tool/js-program/base-class-devops"

        [<Literal>]
        val BaseClassFission: string = "tool/js-program/base-class-fission"

        [<Literal>]
        val BaseClassHorizon: string = "tool/js-program/base-class-horizon"

        [<Literal>]

        [<Literal>]
        val BaseClassAsk: string = "tool/js-program/base-class-ask"

        [<Literal>]
        val BaseClassReview: string = "tool/js-program/base-class-review"

        [<Literal>]
        val BaseClassLifecycle: string = "tool/js-program/base-class-lifecycle"

        [<Literal>]
        val JsPlan: string = "tool/js-plan"

    type Prose =
        { Header: string

          Footer: string
          Contract: string

          ContractParallelEdits: string
          ContractParallelReads: string
          VerbRead: string

          VerbCreate: string
          ReadRules: string
          ReviewOnlyRules: string
          Recipes: string

          WriteRules: string
          MutationRules: string
          Rm: string
          Mv: string
          RunRules: string
          Deliver: string
          DelegationRules: string
          UltraFraming: string
          UltraUnavailable: string
          UltraEngineering: string
          UltraInvestigation: string
          UltraDevOps: string
          UltraManagerInvestigation: string
          UltraPlan: string
          MechanicalSemantic: string

          ReasonRunUnimplemented: string

          Park: string
          ParkedPayload: string
          BaseClass: string
          BaseClassRead: string
          BaseClassAnchors: string
          BaseClassGlob: string
          BaseClassGrep: string
          BaseClassWrite: string
          BaseClassRemove: string
          BaseClassMove: string
          BaseClassExec: string
          BaseClassFork: string
          BaseClassResume: string
          BaseClassCommission: string
          BaseClassDevOps: string
          BaseClassFission: string
          BaseClassHorizon: string
          BaseClassAsk: string
          BaseClassReview: string
          BaseClassLifecycle: string

          JsPlan: string }

    val has: capabilities: Set<JsCapability> -> capability: JsCapability -> bool
    val publicBaseClass: prose: Prose -> capabilities: Set<JsCapability> -> string
    val runtimeBaseClass: prose: Prose -> capabilities: Set<JsCapability> -> string
    val runtimeBaseClassForRole: roleName: string -> prose: Prose -> capabilities: Set<JsCapability> -> string
    val contract: prose: Prose -> toolName: string -> capabilities: Set<JsCapability> -> string
    val rules: prose: Prose -> capabilities: Set<JsCapability> -> string
    val ultraExample: prose: Prose -> roleName: string -> capabilities: Set<JsCapability> -> JsExample option
    val render: prose: Prose -> roleName: string -> toolName: string -> capabilities: Set<JsCapability> -> string
