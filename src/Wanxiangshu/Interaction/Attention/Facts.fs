namespace Wanxiangshu.Interaction.Attention

open Wanxiangshu.Foundation.Identity

type AttentionFactCases =
    | DeferredWorkRecorded of
        {| SessionId: SessionId
           OccurrenceId: string
           Text: string |}
    /// ATTENTION-005/006: the durable consumption receipt of one real terminal.
    /// Every named occurrence stays consumed across restart and journal replay.
    | DeferredWorkConsumed of
        {| SessionId: SessionId
           OccurrenceIds: string list |}
