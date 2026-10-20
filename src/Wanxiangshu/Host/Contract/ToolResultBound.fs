namespace Wanxiangshu.Host.Contract

open Fable.Core.JsInterop
open Wanxiangshu.Foundation

/// Preemptive bound for custom tool results returned to OpenCode Host.
///
/// The Host truncates tool output in `packages/opencode/src/tool/truncate.ts`
/// at a ceiling it declares through `config.tool_output` (`max_lines`,
/// `max_bytes`), falling back to its own defaults when the config is absent:
///
/// ```text
/// default MAX_LINES = 2000
/// default MAX_BYTES = 50 * 1024 = 51200
/// direction default = "head"   // keep start, drop end
/// pass-through iff lines ≤ MAX_LINES AND utf8Bytes ≤ MAX_BYTES
/// lines = text.split("\n").length   // trailing empty counts
/// ```
///
/// We bound before the Host sees the result so it never re-truncates (which
/// would keep the head and drop the decisive tail). We read the live Host
/// ceiling — `HostMaxLines`/`HostMaxBytes`, set from the `config` hook — and
/// keep the **tail**. Marker is a fixed ASCII constant; content budgets derive
/// from the Host ceiling at call time, subtracting the marker's own cost, so
/// `Marker + content` lands exactly at or under the ceiling — no fudge factor:
///
/// ```text
/// Marker        = "...head truncated (tail kept)...\n\n"   // 34 bytes, 2 lines
///
/// When content is non-empty:
///   result = Marker + content
///   result.lines = 2 + content.lines     // "m\n\nx".split → 3 = 2+1
///   result.bytes = 34 + content.bytes
///
/// ContentMaxLines = HostMaxLines − 2
/// ContentMaxBytes = HostMaxBytes − 34
///
/// ⇒ result.lines ≤ HostMaxLines ∧ result.bytes ≤ HostMaxBytes  (Host no-ops)
/// ```
module ToolResultBound =

    // Host transport-layer fallback ceiling (truncate.ts), used only while the
    // Host has declared no `tool_output` limit. It mirrors the transport that
    // receives the result; it is not a self-imposed bound.
    let DefaultHostMaxLines = 2000
    let DefaultHostMaxBytes = 51200

    /// Fixed ASCII prefix. UTF-8 byte length = char length = 34.
    /// Host line cost when concatenated with non-empty content: +2
    /// (`split("\n")` of `"…\n\n" + content` keeps the marker line + one empty).
    let Marker = "...head truncated (tail kept)...\n\n"
    let MarkerBytes = 34
    let MarkerLineCost = 2

    // DSL-MUTABLE: resource — host-declared tool_output ceiling, set once from
    // the `config` hook. Defaults mirror the Host transport; a Host that raises
    // or lowers `tool_output` is followed exactly, never widened by us.
    let mutable private hostMaxLines = DefaultHostMaxLines
    // DSL-MUTABLE: resource — host-declared tool_output ceiling
    let mutable private hostMaxBytes = DefaultHostMaxBytes

    /// The live Host ceiling a bound result must not exceed.
    let HostMaxLines () = hostMaxLines
    let HostMaxBytes () = hostMaxBytes

    /// Content budget: the marker's own cost subtracted from the live ceiling,
    /// so `Marker + tail` lands exactly at or under it.
    let ContentMaxLines () = hostMaxLines - MarkerLineCost
    let ContentMaxBytes () = hostMaxBytes - MarkerBytes

    let private ceilingOf (value: obj) (floor: int) : int option =
        if emitJsExpr (value, floor) "typeof $0 === 'number' && Number.isFinite($0) && $0 > $1" then
            Some(int (unbox<float> value))
        else
            None

    /// Adopt the Host's declared `tool_output` limits. A dimension is taken only
    /// when it is a finite number above the marker's own cost; anything else
    /// (absent, non-number, non-positive, or too small to hold the marker) keeps
    /// the current ceiling, so a malformed config never widens the bound.
    let setHostLimits (maxLines: obj) (maxBytes: obj) : unit =
        match ceilingOf maxLines MarkerLineCost with
        | Some n -> hostMaxLines <- n
        | None -> ()

        match ceilingOf maxBytes MarkerBytes with
        | Some n -> hostMaxBytes <- n
        | None -> ()

    let clearHostLimitsForTests () : unit =
        hostMaxLines <- DefaultHostMaxLines
        hostMaxBytes <- DefaultHostMaxBytes

    let private isHighSurrogate (c: char) =
        let code = int c
        code >= 0xD800 && code <= 0xDBFF

    let private isLowSurrogate (c: char) =
        let code = int c
        code >= 0xDC00 && code <= 0xDFFF

    /// UTF-8 bytes of the scalar at `index` (handles surrogate pairs).
    /// Returns `(codeUnits, bytes)`.
    let private scalarAt (text: string) (index: int) : int * int =
        let c = text.[index]
        let code = int c

        if code < 0x80 then
            1, 1
        elif code < 0x800 then
            1, 2
        elif isHighSurrogate c && index + 1 < text.Length && isLowSurrogate text.[index + 1] then
            2, 4
        else
            // BMP non-ASCII, unpaired surrogate → 3 (matches LlmFacing.byteCount)
            1, 3

    let private scalarStepBack (text: string) (start: int) : int =
        if
            start >= 2
            && isLowSurrogate text.[start - 1]
            && isHighSurrogate text.[start - 2]
        then
            start - 2
        else
            start - 1

    let rec private walkUtf8Tail (text: string) (maxBytes: int) (acc: int) (start: int) : int =
        let atEnd = start <= 0
        let prev = if atEnd then 0 else scalarStepBack text start
        let bytes = if atEnd then 0 else snd (scalarAt text prev)

        if atEnd then start
        elif acc + bytes > maxBytes then start
        else walkUtf8Tail text maxBytes (acc + bytes) prev

    let private sliceFrom (text: string) (start: int) : string =
        if start >= text.Length then "" else text.Substring(start)

    /// UTF-8-safe suffix with byte length ≤ `maxBytes`. Single reverse walk.
    let private utf8Tail (text: string) (maxBytes: int) : string =
        if maxBytes <= 0 then
            ""
        elif LlmFacing.byteCount text <= maxBytes then
            text
        else
            sliceFrom text (walkUtf8Tail text maxBytes 0 text.Length)

    /// Locate segment start (one past previous `\n`) and the newline index (`-1` if none).
    let private findLineBounds (text: string) (segEnd: int) : int * int =
        // DSL-MUTABLE: algorithm-scratch — backward newline-scan cursor
        let mutable p = segEnd - 1

        while p >= 0 && text.[p] <> '\n' do
            p <- p - 1

        p + 1, p

    let private overflowTail (text: string) (segStart: int) (segEnd: int) (maxBytes: int) : string list =
        let tail = utf8Tail (text.Substring(segStart, segEnd - segStart)) maxBytes
        if tail = "" then [] else [ tail ]

    let rec private collectTail
        (text: string)
        (maxLines: int)
        (maxBytes: int)
        (segments: string list)
        (accBytes: int)
        (count: int)
        (segEnd: int)
        : string list =
        let segStart, newlineAt = findLineBounds text segEnd
        let line = text.Substring(segStart, segEnd - segStart)
        let lineBytes = LlmFacing.byteCount line
        let size = lineBytes + (if count = 0 then 0 else 1)
        let overBudget = count >= maxLines || accBytes + size > maxBytes

        if overBudget && count = 0 then
            overflowTail text segStart segEnd maxBytes
        elif overBudget then
            segments
        elif newlineAt < 0 then
            line :: segments
        else
            collectTail text maxLines maxBytes (line :: segments) (accBytes + size) (count + 1) newlineAt

    /// Tail under the supplied budget. Host-compatible accounting: `split('\n')`
    /// keeps empties; inter-line newline costs 1 byte. Single backward walk from
    /// the end — never allocates a full line array.
    let private takeTail (maxLines: int) (maxBytes: int) (text: string) : string =
        String.concat "\n" (collectTail text maxLines maxBytes [] 0 0 text.Length)

    let private newlineBump (n: int) (c: char) = if c = '\n' then n + 1 else n

    let private countLines (text: string) : int =
        // DSL-MUTABLE: algorithm-scratch — newline-derived line counter
        let mutable n = 1

        for c in text do
            n <- newlineBump n c

        n

    let private fitsWith (maxLines: int) (maxBytes: int) (text: string) : bool =
        if isNull text || text = "" then
            true
        else
            let totalBytes = LlmFacing.byteCount text
            let totalLines = countLines text
            totalLines <= maxLines && totalBytes <= maxBytes

    /// Check whether text fits in the live host tool result window without truncation.
    let fitsInWindow (text: string) : bool =
        fitsWith (HostMaxLines()) (HostMaxBytes()) text

    /// Bound a custom tool result against the live Host ceiling. Under the
    /// ceiling → identity. Over → `Marker + tail`, sized so the Host does not
    /// re-truncate.
    let bound (text: string) : string =
        let maxLines = HostMaxLines()
        let maxBytes = HostMaxBytes()

        if fitsWith maxLines maxBytes text then
            text
        else
            Marker + takeTail (maxLines - MarkerLineCost) (maxBytes - MarkerBytes) text
