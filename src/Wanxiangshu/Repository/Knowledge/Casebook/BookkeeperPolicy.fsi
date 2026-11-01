namespace Wanxiangshu.Repository.Knowledge.Casebook

open Wanxiangshu.Foundation
open Wanxiangshu.Participant.Provider

/// Product prose, SDK description and result presentation for Bookkeeper.
/// The casebook library owns edit state and runtime behavior.
[<RequireQualifiedAccess>]
module BookkeeperPolicy =

    /// The semantic prose paths of the tool.
    [<RequireQualifiedAccess>]
    module Path =
        [<Literal>]
        val Description: string = "tool/js-bookkeeper/description"

        [<Literal>]
        val UltraFraming: string = "tool/js-bookkeeper/ultra-framing"

        [<Literal>]
        val ArgProgram: string = "tool/js-bookkeeper/arg-program"

        [<Literal>]
        val Unchanged: string = "tool/js-bookkeeper/unchanged"

        [<Literal>]
        val Accepted: string = "tool/js-bookkeeper/accepted"

        [<Literal>]
        val NoTransaction: string = "tool/js-bookkeeper/no-transaction"

        [<Literal>]
        val ProgramRequired: string = "tool/js-bookkeeper/program-required"

        [<Literal>]
        val SetQuestionOnce: string = "tool/js-bookkeeper/set-question-once"

        [<Literal>]
        val SetQuestionString: string = "tool/js-bookkeeper/set-question-string"

        [<Literal>]
        val SetAnswerOnce: string = "tool/js-bookkeeper/set-answer-once"

        [<Literal>]
        val SetAnswerString: string = "tool/js-bookkeeper/set-answer-string"

    /// The language a call's prose is rendered in. The live global preference
    /// is the only authority (HOST-026), so a call reads it when it runs.
    val callLanguage: unit -> ProviderLanguage

    /// The language the tool description is rendered in. The description is
    /// built once, when the tool is registered, so it reads the observed
    /// preference directly.
    val descriptionLanguage: unit -> ProviderLanguage

    /// The prose of one semantic path.
    val prose: lang: ProviderLanguage -> path: string -> string

    /// The DSL as the provider reads it: the members a program may call.
    val publicBaseClass: string

    /// The worked example that shows how a program reshapes a Case.
    val ultraExample: string

    /// The tool description: the prose, the DSL and the worked example.
    val assembleDescription: lang: ProviderLanguage -> string

    /// The rendered result of a call that changed nothing.
    val failed: lang: ProviderLanguage -> reason: string -> string

    /// The rendered result of a settled call.
    val succeeded: lang: ProviderLanguage -> value: obj -> string
