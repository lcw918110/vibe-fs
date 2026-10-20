namespace Wanxiangshu.Mission.Planning

type ActiveTenureInfo =
    { WorkId: string
      IncumbencyId: string
      Stage: string
      OpeningCursor: int64
      IsFreshHandover: bool }

type TenureMessage =
    { Id: string option
      Role: string
      IsSynthetic: bool
      HasSemanticParts: bool
      Raw: obj }

type TenureAssemblyResult =
    { Messages: obj list
      ReanchorRequested: bool }

module TenureIsolation =

    /// Assembles the physical message set for an active or post-S3 Plan tenure:
    /// verbatim user messages `U` (role user, non-synthetic, in original order)
    /// plus current-tenure physical messages whose first XTrace observation
    /// cursor is `>= tenure.OpeningCursor` (or uncaptured with a stable Host
    /// identity). All prior-tenure assistant/tool messages and synthetic rows
    /// are stripped; handover is carried by the predecessor's ANCHOR view
    /// declaration rather than a machine-materialized record.
    let isCurrentTenurePhysical (openingCursor: int64) (cursor: int64 option) =
        match cursor with
        | None -> true
        | Some sequence -> sequence >= openingCursor

    let private physicalCursor (messageCursors: Map<string, int64>) (message: TenureMessage) =
        message.Id
        |> Option.bind (fun messageId -> Map.tryFind messageId messageCursors)

    /// Strip a captured assistant/tool exchange only when its first
    /// observation is strictly earlier than this opening. A Host id that is
    /// not in the first-observation map has not been captured yet, so the
    /// message stays. A semantic exchange with no Host id is still a contract
    /// violation: do not invent a cursor, and do not keep or drop the whole set.
    let private keepsInteraction
        (tenure: ActiveTenureInfo)
        (identity: string option)
        (cursor: int64 option)
        (hasSemanticParts: bool)
        =
        match identity, cursor, hasSemanticParts with
        | _, Some sequence, _ -> sequence >= tenure.OpeningCursor
        | Some _, None, _ -> true
        | None, None, false -> false
        | None, None, true -> invalidOp "Plan interaction has no stable XTrace message identity"

    let private keepMessage (tenure: ActiveTenureInfo) cursor (message: TenureMessage) =
        match message.IsSynthetic, message.Role with
        | true, _ -> false
        | false, "user" -> true
        | false, "assistant"
        | false, "tool" -> keepsInteraction tenure message.Id cursor message.HasSemanticParts
        | false, _ -> false

    let private validateTenure (tenure: ActiveTenureInfo) =
        if tenure.OpeningCursor < 0L then
            invalidArg (nameof tenure) "Plan opening cursor cannot be negative"

    let assemble
        (messages: TenureMessage list)
        (tenure: ActiveTenureInfo)
        (messageCursors: Map<string, int64>)
        : TenureAssemblyResult =
        validateTenure tenure

        let kept =
            messages
            |> List.choose (fun message ->
                let cursor = physicalCursor messageCursors message

                if keepMessage tenure cursor message then
                    Some message.Raw
                else
                    None)

        { Messages = kept
          ReanchorRequested = tenure.IsFreshHandover }
