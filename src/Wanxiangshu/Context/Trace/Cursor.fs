namespace Wanxiangshu.Context.Trace

/// COMPANION-003 / HOST-005: X 的唯一原始语义轨迹。
///
/// `XTraceCursor` 在 X 生命周期内严格单调，独立于 Host transcript 的临时数组
/// 下标与 semantic turn 编号——Host compaction 会作废后者（HOST-006 重锚），
/// 但 XTrace 与 RecordCoverage 是 durable lifecycle facts，不得随 compaction
/// 清零或重读（COMPANION-008）。因此 cursor 是自增序列，不是 turn/part 坐标。
type XTraceCursor = private XTraceCursor of int64

/// COMPANION-003: 原始记录已覆盖到哪（决定其后未覆盖区间的起点）。可落在 turn 中间。
type RecordCoverage = private RecordCoverage of XTraceCursor

/// A trace-owned half-open interval. Consumers obtain and pass this value as
/// evidence; cursor representation remains owned by `XTraceCursor`.
type XTraceRange = private XTraceRange of startInclusive: XTraceCursor * endExclusive: XTraceCursor

/// Fixed cut boundary from the underlying wanxiangli/trace instance.
type XTraceCut =
    { TraceId: string
      Cursor: int
      Digest: string }

/// Stable copied Opening proof. Defined with the foundational trace vocabulary
type XTraceOpeningEvidence =
    { AssignmentText: string
      AuthoritativeRequirements: string list
      ConstitutiveBody: string }

[<RequireQualifiedAccess>]
module XTraceCursor =
    let originCursor = XTraceCursor 0L

    /// Rehydrate a durable cursor sequence at an owner boundary.
    let create (sequence: int64) : XTraceCursor =
        if sequence < 0L then
            invalidArg (nameof sequence) "XTrace cursor sequence cannot be negative"

        XTraceCursor sequence

    /// Stable serialization/query operation. Consumers must not inspect the
    /// cursor record representation directly.
    let sequence (XTraceCursor sequence) = sequence

    let nextCursor (XTraceCursor sequence) : XTraceCursor = XTraceCursor(sequence + 1L)

    /// 严格单调比较。同 cursor 重复 append 是 PERSIST-010 的拒绝条件。
    let isAfter (next: XTraceCursor) (previous: XTraceCursor) = sequence next > sequence previous

    let isAtOrAfter (cursor: XTraceCursor) (lowerBound: XTraceCursor) = sequence cursor >= sequence lowerBound

    let isBefore (cursor: XTraceCursor) (upperBound: XTraceCursor) = sequence cursor < sequence upperBound

    let maxCursor (left: XTraceCursor) (right: XTraceCursor) =
        if isAfter left right then left else right

[<RequireQualifiedAccess>]
module RecordCoverage =
    let create (ingestedThrough: XTraceCursor) : RecordCoverage = RecordCoverage ingestedThrough

    let ingestedThrough (RecordCoverage ingestedThrough) = ingestedThrough

    let covers (cursor: XTraceCursor) (coverage: RecordCoverage) =
        not (XTraceCursor.isAfter cursor (ingestedThrough coverage))

    let firstUncoveredCursor (coverage: RecordCoverage) : XTraceCursor =
        XTraceCursor.nextCursor (ingestedThrough coverage)

[<RequireQualifiedAccess>]
module XTraceRange =
    let create (startInclusive: XTraceCursor) (endExclusive: XTraceCursor) : XTraceRange =
        if XTraceCursor.isAfter startInclusive endExclusive then
            invalidArg (nameof endExclusive) "XTrace range end cannot precede its start"

        XTraceRange(startInclusive, endExclusive)

    let startInclusive (XTraceRange(startInclusive, _)) = startInclusive

    let endExclusive (XTraceRange(_, endExclusive)) = endExclusive

    let contains (cursor: XTraceCursor) (range: XTraceRange) =
        XTraceCursor.isAtOrAfter cursor (startInclusive range)
        && XTraceCursor.isBefore cursor (endExclusive range)

    let isEmpty (range: XTraceRange) =
        startInclusive range = endExclusive range

    let afterCoverage (coverage: RecordCoverage) (openingFloor: XTraceCursor) (head: XTraceCursor) : XTraceRange =
        let rawStart =
            XTraceCursor.maxCursor (RecordCoverage.ingestedThrough coverage) openingFloor

        let clampedStart =
            if XTraceCursor.isAfter rawStart head then
                head
            else
                rawStart

        create clampedStart head
