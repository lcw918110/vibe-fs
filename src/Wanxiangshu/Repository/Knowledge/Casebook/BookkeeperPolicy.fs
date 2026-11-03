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
        let Description = "tool/js-bookkeeper/description"

        [<Literal>]
        let UltraFraming = "tool/js-bookkeeper/ultra-framing"

        [<Literal>]
        let ArgProgram = "tool/js-bookkeeper/arg-program"

        [<Literal>]
        let Unchanged = "tool/js-bookkeeper/unchanged"

        [<Literal>]
        let Accepted = "tool/js-bookkeeper/accepted"

        [<Literal>]
        let NoTransaction = "tool/js-bookkeeper/no-transaction"

        [<Literal>]
        let ProgramRequired = "tool/js-bookkeeper/program-required"

        [<Literal>]
        let SetQuestionOnce = "tool/js-bookkeeper/set-question-once"

        [<Literal>]
        let SetQuestionString = "tool/js-bookkeeper/set-question-string"

        [<Literal>]
        let SetAnswerOnce = "tool/js-bookkeeper/set-answer-once"

        [<Literal>]
        let SetAnswerString = "tool/js-bookkeeper/set-answer-string"

    /// The language a call's prose is rendered in. The live global preference
    /// is the only authority (HOST-026), so a call reads it when it runs.
    let callLanguage () : ProviderLanguage = GlobalProviderLanguage.current ()

    /// The language the tool description is rendered in. The description is
    /// built once, when the tool is registered, so it reads the observed
    /// preference directly.
    let descriptionLanguage () : ProviderLanguage =
        ProviderLanguageBinding.readGlobalPreference ()

    /// The prose of one semantic path.
    let prose (lang: ProviderLanguage) (path: string) : string =
        ProviderProse.render lang path Map.empty

    /// The DSL as the provider reads it: the members a program may call.
    let publicBaseClass =
        """class JsProgram {
  question(matches = []) {
  }

  answer(matches = []) {
  }

  setQuestion(newText) {
  }

  setAnswer(newText) {
  }

  async run() {
    throw new Error("JS_RUN_NOT_IMPLEMENTED");
  }
}"""

    /// The worked example that shows how a program reshapes a Case.
    let ultraExample =
        """class Js extends JsProgram {
  async run() {
    const question = this.question([
      ['constraints', 'afterConstraints', '## Constraints'],
    ]);
    const answer = this.answer([
      ['answer', 'afterAnswer', '## Answer'],
      ['evidence', 'afterEvidence', '## Evidence'],
    ]);

    const asksForCompatibility = /backward compatibility/i.test(question.text());
    const claimsCompatibility = /backward compatible|no breaking change/i.test(answer.text());
    const hasEvidence = /compatibility test|legacy client/i.test(answer.text('evidence', '$'));

    if (!claimsCompatibility || !asksForCompatibility || hasEvidence)
      return { changed: false };

    this.setQuestion(
      question.text('^', 'constraints')
        + '## Constraints\n'
        + question.text('afterConstraints', '$')
        + '\n\nClarify whether backward compatibility is required.\n'
    );
    this.setAnswer(
      '## Answer\nThe implementation result is established, but backward compatibility is not established by the supplied evidence.\n'
        + answer.text('afterAnswer', '$')
    );

    return { changed: true };
  }
}"""

    /// The tool description: the prose, the DSL and the worked example.
    let assembleDescription (lang: ProviderLanguage) : string =
        String.concat
            "\n\n"
            [ prose lang Path.Description
              "```js\n" + publicBaseClass + "\n```"
              prose lang Path.UltraFraming
              "```js\n" + ultraExample + "\n```" ]

    /// The rendered result of a call that changed nothing.
    let failed (lang: ProviderLanguage) (reason: string) : string =
        LlmFacing.instructions [ prose lang Path.Unchanged; reason ] |> LlmFacing.render

    /// The rendered result of a settled call.
    let succeeded (lang: ProviderLanguage) (value: obj) : string =
        LlmFacing.instruction (prose lang Path.Accepted)
        |> LlmFacing.withData (LlmFacing.Data.structuredValue (LlmFacing.Data.ofJs value))
        |> LlmFacing.render
