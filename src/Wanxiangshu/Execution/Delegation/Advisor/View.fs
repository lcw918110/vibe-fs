namespace Wanxiangshu.Execution.Delegation.Advisor

open Wanxiangshu.Foundation
open Wanxiangshu.Participant.Provider

/// advisor-[002]: one record of the caller's own context, addressed by the caller's anchor number.
type AdvisorViewMessage = { Anchor: int; Text: string }

/// advisor-[003]: how far this request may reach, and which anchors arrive as summaries.
type AdvisorViewDeclaration =
    { PrefixUntil: int
      ThenRemove: (int * string) list }

/// advisor-[002]: the caller's context, in order, plus the declaration it is read under.
type AdvisorViewRequest =
    { Messages: AdvisorViewMessage list
      Declaration: AdvisorViewDeclaration }

/// advisor-[002]/[003]: the one prompt this request hands over, and the cursor it moves.
type AdvisorViewPlan =
    { ViewPrompt: string
      DeliveredAnchors: int list
      HasNewMaterial: bool }

/// advisor-[002]/[003]: render the caller's context view as the advisor seat's first prompt.
///
/// The prompt is this seat's own view. Foundation's `SyntheticToml` owns the bytes (ARCH-010):
/// the authored `# ` comment header, one blank line, then one `[[new_work_to_record]]` table per
/// record, with the record text under the field `record`. That table name is this view's schema.
///
/// Selection is by anchor alone: an anchor is kept when it is below the declaration's `PrefixUntil`
/// and was not delivered yet, and a `ThenRemove` pair replaces that anchor's text with its summary.
/// The plan's `DeliveredAnchors` is the union of what this prompt hands over with what the seat
/// already held, so a follow-up carries the delta alone (advisor-[003]).
///
/// Pure: no Host, no journal, no clock. The same request and language always render the same bytes,
/// and no map order reaches the prompt — the caller's anchors are sorted, once.
[<RequireQualifiedAccess>]
module AdvisorView =

    /// ARCH-010: the authored header for this view, one semantic path for both locales.
    let private viewPath = "lifecycle/advisor/view"

    /// This view's own table. One record, one table; the text sits under `record`.
    let private recordTable = "new_work_to_record"

    /// The record's text under the table's own field name.
    let private recordField = "record"

    let private headerLines (language: string) =
        ProviderProse.instructionLines (ProviderLanguage.parse language) viewPath Map.empty

    /// advisor-[003]: a `ThenRemove` pair replaces that anchor's text with its summary.
    let private textOf (removals: Map<int, string>) (message: AdvisorViewMessage) =
        removals |> Map.tryFind message.Anchor |> Option.defaultValue message.Text

    let private recordBlock (text: string) =
        SyntheticToml.tableArrayEntry recordTable [ SyntheticToml.field recordField (SyntheticToml.renderString text) ]

    /// advisor-[003]: the delta — anchors below `PrefixUntil` that were not delivered yet.
    let private delta (delivered: Set<int>) (request: AdvisorViewRequest) =
        request.Messages
        |> List.filter (fun message ->
            message.Anchor < request.Declaration.PrefixUntil
            && not (Set.contains message.Anchor delivered))
        |> List.sortBy (fun message -> message.Anchor)

    let private anchors (messages: AdvisorViewMessage list) =
        messages |> List.map (fun message -> message.Anchor)

    let plan (language: string) (delivered: Set<int>) (request: AdvisorViewRequest) : AdvisorViewPlan =
        let emitted = delta delivered request
        let removals = Map.ofList request.Declaration.ThenRemove

        let viewPrompt =
            emitted
            |> List.map (fun message -> recordBlock (textOf removals message))
            |> SyntheticToml.document (headerLines language)

        { ViewPrompt = viewPrompt
          DeliveredAnchors = List.sort (anchors emitted @ Set.toList delivered)
          HasNewMaterial = not (List.isEmpty emitted) }
