namespace Wanxiangshu.Foundation

open System
open System.Text

/// ARCH-010: the one canonical writer for runtime synthetic TOML.
///
/// The clause says 「字符串写法只有一个 owner。各业务模块不得分别决定引号、转义、换行处理、缩进或
/// closing delimiter 位置」. Until N3 that was true only incidentally — previously Toml was the sole
/// producer, so it could hold the rules privately and still satisfy the clause. The moment a second
/// surface needs to render a value, "only one owner" has to become structural or the second surface
/// copies the logic and the dialect the clause forbids exists.
///
/// So this module owns the string rules and the document layout, and every synthetic surface renders
/// through it. It knows nothing about forks or any local schema: those live with their producers.
///
/// There is deliberately no parser. ARCH-010 forbids business logic that reads this text back.
[<RequireQualifiedAccess>]
module SyntheticToml =

    /// Normalize instruction layout; data values retain their original line endings.
    let normalizeNewlines (text: string) =
        if isNull text then
            ""
        else
            text.Replace("\r\n", "\n").Replace("\r", "\n")

    let private appendEscaped (sb: StringBuilder) (ch: char) =
        match ch with
        | '"' -> sb.Append "\\\"" |> ignore
        | '\\' -> sb.Append "\\\\" |> ignore
        | '\b' -> sb.Append "\\b" |> ignore
        | '\t' -> sb.Append "\\t" |> ignore
        | '\n' -> sb.Append "\\n" |> ignore
        | '\f' -> sb.Append "\\f" |> ignore
        | '\r' -> sb.Append "\\r" |> ignore
        | c when c < ' ' || c = '\u007F' -> sb.Append(sprintf "\\u%04X" (int c)) |> ignore
        | c -> sb.Append c |> ignore

    let private escapeBasic (text: string) =
        let sb = StringBuilder()

        for ch in text do
            appendEscaped sb ch

        sb.ToString()

    let private advanceLiteralSafety (c: char) (quoteRun: int) =
        if c = '\'' then
            let nextRun = quoteRun + 1
            nextRun, nextRun < 3
        elif c <> '\n' && c <> '\t' && Char.IsControl c then
            0, false
        else
            0, true

    /// Can this text sit inside `'''…'''` unchanged?
    ///
    /// A literal multi-line string processes NO escapes, which is the only way to carry code, JSON
    /// and logs verbatim. Two things it cannot hold: `'''`, which would close it early and let the
    /// remainder escape into the document structure, and raw control characters other than tab and
    /// newline, which TOML forbids in any string.
    ///
    /// It used to also reject a trailing `'`, because a closing delimiter written immediately after
    /// the last content character formed `''''`. ARCH-010 puts the delimiter on its own line, so
    /// that case cannot arise and the check is gone rather than kept "for safety" — a predicate
    /// nothing can fail is indistinguishable from one that is wrong.
    let private literalSafeRange (text: string) (origin: int) (stop: int) =
        // DSL-MUTABLE: algorithm-scratch — literal-safe index cursor
        let mutable index = origin
        // DSL-MUTABLE: algorithm-scratch — consecutive single-quote run
        let mutable quoteRun = 0
        // DSL-MUTABLE: algorithm-scratch — still-safe flag
        let mutable safe = true

        while safe && index < stop do
            let nextRun, stillSafe = advanceLiteralSafety text.[index] quoteRun
            quoteRun <- nextRun
            safe <- stillSafe
            index <- index + 1

        safe

    let private literalSafe (text: string) = literalSafeRange text 0 text.Length

    /// A literal's closing delimiter stays on its own line only when the value
    /// already ends in LF. Other values use basic escapes without changing data.
    let renderString (raw: string) : string =
        let text = if isNull raw then "" else raw

        if text.EndsWith "\n" && literalSafe text then
            "'''\n" + text + "'''"
        else
            "\"" + escapeBasic text + "\""

    /// One instruction line, or several if the text itself spans lines.
    ///
    /// Splitting is not a convenience: a `\n` inside a comment would end the comment and put the
    /// remainder at top level as syntax, which is the containment failure ARCH-010 names. Blank
    /// lines render as a bare `#` so the header stays one contiguous comment block — a truly empty
    /// line would end the header and make everything after it a second, illegal one.
    let comment (text: string) : string =
        normalizeNewlines text
        |> fun normalized -> normalized.Split '\n'
        |> Array.map (fun line -> if line = "" then "#" else "# " + line)
        |> String.concat "\n"

    /// `name = <rendered value>`. The value must already be rendered by `renderString`.
    let field (name: string) (renderedValue: string) : string = name + " = " + renderedValue

    /// A `[name]` table: header plus its fields, as one block.
    let tableEntry (name: string) (fields: string list) : string =
        String.concat "\n" (("[" + name + "]") :: fields)

    /// A `[[name]]` entry: the header plus its own fields, as one block.
    ///
    /// Takes the fields rather than returning a bare header so an entry cannot be assembled with
    /// something else accidentally between the header and its body — which in TOML would silently
    /// reassign those fields to a different table.
    let tableArrayEntry (name: string) (fields: string list) : string =
        String.concat "\n" (("[[" + name + "]]") :: fields)

    let renderBool (value: bool) = if value then "true" else "false"

    let renderInt (value: int64) = string value

    /// Non-integer finite floats only. If the runtime string has no decimal and
    /// no exponent, append `.0` so the token cannot be read as an integer.
    let renderFloat (value: float) =
        let text = string value

        if text.IndexOf '.' >= 0 || text.IndexOf 'e' >= 0 || text.IndexOf 'E' >= 0 then
            text
        else
            text + ".0"

    let private isBareKey (name: string) =
        name.Length > 0
        && name
           |> Seq.forall (fun c ->
               (c >= 'A' && c <= 'Z')
               || (c >= 'a' && c <= 'z')
               || (c >= '0' && c <= '9')
               || c = '_'
               || c = '-')

    let renderKey (name: string) =
        if isBareKey name then
            name
        else
            "\"" + escapeBasic name + "\""

    let private formatPath (segments: string list) =
        segments |> List.map renderKey |> String.concat "."

    let encodeFs
        (rewritten: string list)
        (created: string list)
        (removed: string list)
        (moved: (string * string) list)
        : string list =
        let renderStringArray values =
            "[" + String.concat ", " (List.map renderString values) + "]"

        let renderMovedArray values =
            "["
            + String.concat
                ", "
                (values
                 |> List.map (fun (fromPath, toPath) -> renderString (fromPath + " -> " + toPath)))
            + "]"

        let fields =
            [ if rewritten <> [] then
                  field "rewritten" (renderStringArray rewritten)
              if created <> [] then
                  field "created" (renderStringArray created)
              if removed <> [] then
                  field "removed" (renderStringArray removed)
              if moved <> [] then
                  field "moved" (renderMovedArray moved) ]

        if fields = [] then [] else [ tableEntry "fs" fields ]

    /// Is this block a table header rather than a bare field?
    ///
    /// Reads the block's FIRST LINE, not the block. A multi-line value whose content begins with `[`
    /// — a log line, a JSON array, a rendered table — starts with `key = '''`, so it is correctly
    /// read as a field. Testing the whole block would misclassify exactly the payloads ARCH-010's
    /// containment rule exists to protect.
    let private isTableBlock (block: string) =
        let firstLine = (block.Split '\n').[0]
        firstLine.StartsWith "[" && firstLine.EndsWith "]"

    /// Assemble a payload: instruction comment header, one blank line, data body.
    ///
    /// This is where ARCH-010's layout rules stop being something a producer has to remember:
    ///
    ///   instruction-first        the header is a separate argument and always emitted first
    ///   exactly one blank line   inserted here, so no producer can pick a different spacing
    ///   no comment in the body   unexpressible: body blocks come from `field` and `tableArrayEntry`
    ///   three legal shapes       an empty header gives data-only, an empty body instruction-only,
    ///                            and neither adds the separator it would otherwise need
    ///   no body blank lines      the data body renders with single LF between blocks; only the
    ///                            header/body boundary carries the one blank line (ARCH-010).
    ///                            Multi-line TOML string values are content and keep their own
    ///                            newlines untouched.
    ///
    /// ── bare fields are emitted before tables, and that is load-bearing ─────
    ///
    /// In TOML a bare `key = value` after a `[[table]]` header belongs to THAT TABLE, not to the
    /// document. Measured: `[[t]]\nx = 2\n\na = 1` parses as `t = [{ x = 2, a = 1 }]` — the field is
    /// silently absorbed, with no error and no visible difference in the text.
    ///
    /// A composer that appends a top-level field after a table array therefore produces a document
    /// whose meaning is not what it reads like, and the failure is invisible in exactly the direction
    /// that matters: the payload still renders, still parses, and the model still sees the words.
    /// Sorting here makes the mistake unexpressible instead of asking every producer to remember it.
    ///
    /// The sort is stable, so a producer's own field order and table order both survive.
    let document (instructions: string list) (body: string list) : string =
        let header = instructions |> List.map comment
        let blocks = body |> List.filter (fun block -> block <> "")
        let bare, tables = blocks |> List.partition (isTableBlock >> not)
        let ordered = bare @ tables

        match header, ordered with
        | [], [] -> ""
        | _, [] -> String.concat "\n" header + "\n"
        | [], _ -> String.concat "\n" ordered + "\n"
        | _, _ -> String.concat "\n" header + "\n\n" + String.concat "\n" ordered + "\n"

    let private isUtf16LowSurrogate (code: int) = code >= 0xDC00 && code <= 0xDFFF

    let private isUtf16HighSurrogate (code: int) = code >= 0xD800 && code <= 0xDBFF

    let private utf8Step (text: string) (index: int) (stop: int) =
        let code = int text.[index]

        if code < 0x80 then
            1, 1
        elif code < 0x800 then
            2, 1
        elif
            code >= 0xD800
            && code <= 0xDBFF
            && index + 1 < stop
            && isUtf16LowSurrogate (int text.[index + 1])
        then
            4, 2
        else
            3, 1

    let private byteCountRange (text: string) (origin: int) (stop: int) : int =
        // DSL-MUTABLE: algorithm-scratch — span byte-count total accumulator
        let mutable total = 0
        // DSL-MUTABLE: algorithm-scratch — span byte-count index cursor
        let mutable index = origin

        while index < stop do
            let width, advance = utf8Step text index stop
            total <- total + width
            index <- index + advance

        total

    /// Byte length of `escapeBasic` over `[origin, stop)` without allocating the
    /// escaped string. Arms and UTF-8 pairing must stay in lockstep with `escapeBasic`.
    let private escapeBasicByteStep (text: string) (index: int) (stop: int) =
        let code = int text.[index]

        if
            code = 0x22
            || code = 0x5C
            || code = 0x08
            || code = 0x09
            || code = 0x0A
            || code = 0x0C
            || code = 0x0D
        then
            2, 1
        elif code < 0x20 || code = 0x7F then
            6, 1
        elif code < 0x80 then
            1, 1
        elif code < 0x800 then
            2, 1
        elif
            code >= 0xD800
            && code <= 0xDBFF
            && index + 1 < stop
            && isUtf16LowSurrogate (int text.[index + 1])
        then
            4, 2
        else
            3, 1

    let private escapeBasicByteCountRange (text: string) (origin: int) (stop: int) : int =
        // DSL-MUTABLE: algorithm-scratch — escaped-byte total accumulator
        let mutable total = 0
        // DSL-MUTABLE: algorithm-scratch — escaped-byte index cursor
        let mutable index = origin

        while index < stop do
            let width, advance = escapeBasicByteStep text index stop
            total <- total + width
            index <- index + advance

        total

    let private tripleQuoteJoins (head: string) (headLen: int) (tail: string) =
        let total = headLen + tail.Length

        let charAt i =
            if i < 0 || i >= total then Char.MinValue
            elif i < headLen then head.[i]
            else tail.[i - headLen]

        let startsAt i =
            charAt i = '\'' && charAt (i + 1) = '\'' && charAt (i + 2) = '\''

        (headLen >= 2 && startsAt (headLen - 2))
        || (headLen >= 1 && startsAt (headLen - 1))

    let private joinedUtf8Adjustment (head: string) (headLen: int) (tail: string) =
        if
            headLen > 0
            && tail.Length > 0
            && isUtf16HighSurrogate (int head.[headLen - 1])
            && isUtf16LowSurrogate (int tail.[0])
        then
            -2
        else
            0

    /// UTF-8 byte length of `renderString (text.Substring(0, length) + suffix)`
    /// without allocating the concatenation or the rendered form.
    let renderStringByteCountPrefix (text: string) (length: int) (suffix: string) : int =
        let text = if isNull text then "" else text
        let suffix = if isNull suffix then "" else suffix

        let headLen =
            if length < 0 then 0
            elif length > text.Length then text.Length
            else length

        let endsWithNewline =
            if suffix.Length > 0 then
                suffix.EndsWith "\n"
            else
                headLen > 0 && text.[headLen - 1] = '\n'

        let safe =
            literalSafeRange text 0 headLen
            && literalSafeRange suffix 0 suffix.Length
            && not (tripleQuoteJoins text headLen suffix)

        let joinAdjustment = joinedUtf8Adjustment text headLen suffix

        if endsWithNewline && safe then
            7
            + byteCountRange text 0 headLen
            + byteCountRange suffix 0 suffix.Length
            + joinAdjustment
        else
            2
            + escapeBasicByteCountRange text 0 headLen
            + escapeBasicByteCountRange suffix 0 suffix.Length
            + joinAdjustment

    /// UTF-8 byte count of rendered text.
    ///
    /// The limits every synthetic surface is measured against are byte limits, not character counts:
    /// a CJK-heavy payload is three times its character count here.
    ///
    /// Counted by hand rather than through `Encoding.UTF8`: Fable does not implement `GetByteCount`,
    /// and `GetBytes(...).Length` would allocate the whole buffer on every step of a truncation
    /// search. The arithmetic is UTF-8's definition — 1 byte below U+0080, 2 below U+0800, 4 for a
    /// surrogate pair, 3 otherwise.
    ///
    /// An unpaired surrogate counts as 3, matching what both runtimes emit for the U+FFFD
    /// replacement they substitute.
    let byteCount (text: string) : int =
        if isNull text then 0 else byteCountRange text 0 text.Length
