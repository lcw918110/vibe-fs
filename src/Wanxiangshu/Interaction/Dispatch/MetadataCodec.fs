namespace Wanxiangshu.Interaction.Dispatch

open Fable.Core.JsInterop
open Wanxiangshu.Foundation.Identity

/// Metadata sent with a prompt and read back from Host messages.
/// PromptKey correlates recovery; origin selects continuation and delivery behavior.
/// LogicalRunId records the run when one exists. These fields are not provider text.
module PromptMetadataCodec =
    [<Literal>]
    let PromptKeyField = "wanxiangshu_prompt_key"

    [<Literal>]
    let OriginField = "wanxiangshu_origin"

    [<Literal>]
    let LogicalRunField = "wanxiangshu_logical_run"

    let create (key: PromptKey) (origin: string) (logicalRunId: LogicalRunId option) : obj =
        createObj
            [ PromptKeyField, box (PromptKey.value key)
              OriginField, box origin
              // An Authority Root claim has no run yet: the run id derives from a
              // physical message the Host has not created. Emitting "" would make
              // "no run" and "a run named empty" the same value on the wire.
              LogicalRunField,
              box (
                  match logicalRunId with
                  | Some runId -> LogicalRunId.value runId
                  | None -> null
              ) ]
