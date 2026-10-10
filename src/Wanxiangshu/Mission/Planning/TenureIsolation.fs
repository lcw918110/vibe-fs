namespace Wanxiangshu.Mission.Planning

open System
open Fable.Core
open Fable.Core.JsInterop
open Wanxiangshu.Context.Trace

type ActiveTenureInfo =
    { WorkId: string
      IncumbencyId: string
      Stage: string
      OpeningCursor: int64
      PreviousRange: (int64 * int64) option
      IsFreshHandover: bool }

type TenureMessage =
    { Id: string option
      Role: string
      Content: string
      Cursor: int64 option
      Raw: obj }

type TenureAssemblyResult =
    { Messages: obj list
      ReanchorRequested: bool }

module TenureIsolation =

    let private readField (value: obj) (name: string) : obj =
        if isNull value then
            null
        else
            emitJsExpr (value, name) "$0[$1]"

    let private parseStringToInt64 (str: string) : int64 =
        match Int64.TryParse str with
        | true, v -> v
        | false, _ -> 0L

    let private toInt64 (value: obj) : int64 =
        if isNull value then
            0L
        elif emitJsExpr value "typeof $0 === 'bigint'" then
            unbox<int64> value
        elif emitJsExpr value "typeof $0 === 'number'" then
            int64 (unbox<int> value)
        elif emitJsExpr value "typeof $0 === 'string'" then
            parseStringToInt64 (unbox<string> value)
        else
            0L

    let private extractPartsContent (raw: obj) : string =
        let parts = readField raw "parts"

        if not (isNull parts) && emitJsExpr parts "Array.isArray($0)" then
            emitJsExpr parts "$0.map(function(p){ return p && p.text ? p.text : ''; }).filter(Boolean).join('\\n')"
            |> unbox<string>
        else
            ""

    let private extractContent (raw: obj) : string =
        let c = readField raw "content"

        if not (isNull c) && emitJsExpr c "typeof $0 === 'string'" then
            unbox<string> c
        else
            extractPartsContent raw

    let private extractCursor (raw: obj) (info: obj) : int64 option =
        let c = readField raw "cursor"
        let cOpt = if isNull c then readField info "cursor" else c
        let seq = if isNull cOpt then readField raw "sequence" else cOpt

        if isNull seq || emitJsExpr seq "$0 == null" then
            None
        else
            Some(toInt64 seq)

    let private extractId (raw: obj) (info: obj) : string option =
        let idVal = readField info "id"
        let idVal2 = if isNull idVal then readField raw "id" else idVal
        if isNull idVal2 then None else Some(unbox<string> idVal2)

    let private extractRole (raw: obj) (info: obj) : string =
        let roleVal = readField info "role"
        let roleVal2 = if isNull roleVal then readField raw "role" else roleVal

        if isNull roleVal2 then
            ""
        else
            (unbox<string> roleVal2).ToLowerInvariant()

    let messageOfRaw (raw: obj) : TenureMessage =
        if isNull raw then
            { Id = None
              Role = "unknown"
              Content = ""
              Cursor = None
              Raw = raw }
        else
            let info = readField raw "info"
            let id = extractId raw info
            let role = extractRole raw info
            let content = extractContent raw
            let cursor = extractCursor raw info

            { Id = id
              Role = role
              Content = content
              Cursor = cursor
              Raw = raw }

    let private getProp (raw: obj) (names: string list) : obj =
        names
        |> List.tryPick (fun n ->
            let v = readField raw n
            if isNull v then None else Some v)
        |> Option.toObj

    let private parsePreviousRange (raw: obj) : (int64 * int64) option =
        let v = getProp raw [ "previousRange"; "PreviousRange" ]

        if isNull v || emitJsExpr v "$0 == null" then
            None
        elif emitJsExpr v "Array.isArray($0) && $0.length >= 2" then
            let s = emitJsExpr v "$0[0]"
            let e = emitJsExpr v "$0[1]"
            Some(toInt64 s, toInt64 e)
        else
            None

    let private extractOpeningCursor (raw: obj) : int64 =
        let v = getProp raw [ "openingCursor"; "OpeningCursor" ]

        if isNull v || emitJsExpr v "$0 == null" then
            0L
        else
            toInt64 v

    let tenureOfRaw (raw: obj) : ActiveTenureInfo =
        if isNull raw then
            { WorkId = ""
              IncumbencyId = ""
              Stage = "S1"
              OpeningCursor = 0L
              PreviousRange = None
              IsFreshHandover = true }
        else
            let wId =
                let v = getProp raw [ "workId"; "WorkId" ]
                if isNull v then "" else unbox<string> v

            let iId =
                let v = getProp raw [ "incumbencyId"; "IncumbencyId" ]
                if isNull v then "" else unbox<string> v

            let st =
                let v = getProp raw [ "stage"; "Stage" ]
                if isNull v then "S1" else unbox<string> v

            let opCursor = extractOpeningCursor raw

            let prevRange = parsePreviousRange raw

            let fresh =
                let v = getProp raw [ "isFreshHandover"; "IsFreshHandover" ]
                if isNull v then true else unbox<bool> v

            { WorkId = wId
              IncumbencyId = iId
              Stage = st
              OpeningCursor = opCursor
              PreviousRange = prevRange
              IsFreshHandover = fresh }

    let private createRecordMessage (incumbencyId: string) (lwrText: string) : obj =
        box
            {| id = "lwr-prev-" + incumbencyId
               role = "user"
               synthetic = true
               content = lwrText
               parts = [| box {| ``type`` = "text"; text = lwrText |} |]
               info =
                {| id = "lwr-prev-" + incumbencyId
                   role = "user"
                   synthetic = true |} |}

    let private checkIsCurrentTenure (tm: TenureMessage) (openingCursor: int64) (hasPrevRange: bool) : bool =
        match tm.Role, tm.Cursor with
        | ("user" | "system"), _ -> false
        | _, Some c -> c > openingCursor
        | _, None -> not hasPrevRange

    let private renderLwrMessage (incumbencyId: string) (lwrText: string) : obj option =
        if String.IsNullOrWhiteSpace lwrText then
            None
        else
            Some(createRecordMessage incumbencyId lwrText)

    let private materializePreviousTenure
        (incumbencyId: string)
        (prevRangeOpt: (int64 * int64) option)
        (materializePrev: obj -> string)
        : obj option =
        match prevRangeOpt with
        | None -> None
        | Some(startC, endC) ->
            let rangeObj = box {| start = startC; ``end`` = endC |}
            let lwrText = materializePrev rangeObj
            renderLwrMessage incumbencyId lwrText

    let private tryUnboxList (v: obj) : obj list =
        try
            unbox<obj list> v
        with _ ->
            []

    let private parseRawMessages (raw: obj) : obj list =
        if isNull raw then
            []
        elif emitJsExpr raw "Array.isArray($0)" then
            unbox<obj array> raw |> Array.toList
        else
            tryUnboxList raw

    let assembleTenureMessages (rawMessagesInput: obj) (tenureInput: obj) (materializePrev: obj -> string) : obj =
        let rawMessages = parseRawMessages rawMessagesInput

        let tenure =
            match tenureInput with
            | :? ActiveTenureInfo as t -> t
            | _ -> tenureOfRaw tenureInput

        let parsed = rawMessages |> List.map messageOfRaw

        // 1. U: all user messages in causal scope preserved verbatim in original order
        let uMessages =
            List.zip rawMessages parsed
            |> List.filter (fun (_, tm) -> tm.Role = "user")
            |> List.map fst

        // 2. Current tenure messages: assistant/tool messages after openingCursor
        let currentAssistantToolMessages =
            List.zip rawMessages parsed
            |> List.filter (fun (_, tm) ->
                (tm.Role = "assistant" || tm.Role = "tool")
                && checkIsCurrentTenure tm tenure.OpeningCursor tenure.PreviousRange.IsSome)
            |> List.map fst

        // 3. First request in tenure detection:
        let hasCurrentAssistantOrTool = not (List.isEmpty currentAssistantToolMessages)
        let reanchorRequested = tenure.IsFreshHandover || not hasCurrentAssistantOrTool

        // 4. LWR_prev: for non-initial incumbencies, materialize previous tenure's range
        let lwrMessageOpt =
            materializePreviousTenure tenure.IncumbencyId tenure.PreviousRange materializePrev

        // 5. Assemble: U + [LWR_prev] + current messages (prior assistant/tool stripped)
        let assembled =
            match lwrMessageOpt with
            | Some lwrMsg -> uMessages @ [ lwrMsg ] @ currentAssistantToolMessages
            | None -> uMessages @ currentAssistantToolMessages

        box
            {| messages = assembled |> List.toArray
               reanchorRequested = reanchorRequested |}

    let private isSyntheticUserMessage (raw: obj) (tm: TenureMessage) : bool =
        let syn = readField raw "synthetic"
        let isSyn = not (isNull syn) && unbox<bool> syn
        let idStr = defaultArg tm.Id ""
        isSyn || idStr.StartsWith "lwr-prev-"

    let private hasCursorAfter (pendingCursor: int64) (tm: TenureMessage) : bool =
        match tm.Cursor with
        | Some c -> c > pendingCursor
        | None -> false

    let private isUserAnswer (pendingCursor: int64) (raw: obj, tm: TenureMessage) : bool =
        tm.Role = "user"
        && not (isSyntheticUserMessage raw tm)
        && hasCursorAfter pendingCursor tm

    let private createAskToolMessage (replyTm: TenureMessage) : obj =
        let userText = replyTm.Content
        let msgId = "ask-result-" + (defaultArg replyTm.Id (Guid.NewGuid().ToString("N")))

        box
            {| id = msgId
               role = "tool"
               name = "ask"
               content = userText
               parts =
                [| box
                       {| ``type`` = "tool_result"
                          tool = "ask"
                          text = userText |} |]
               info = {| role = "tool"; name = "ask" |} |}

    let private filterOrReplaceMessage
        (replyIdx: int)
        (toolMsg: obj)
        (i: int, raw: obj, tm: TenureMessage)
        : obj option =
        if i = replyIdx then Some toolMsg
        elif i < replyIdx && tm.Role = "assistant" then None
        else Some raw

    let private extractPendingAskRecord (q: obj) (cur: obj) (inc: obj) =
        if isNull q || emitJsExpr q "$0 == null" then
            None
        else
            Some
                {| Question = unbox<string> q
                   Cursor = if isNull cur then 0L else toInt64 cur
                   IncumbencyId = if isNull inc then "" else unbox<string> inc |}

    let private parsePendingAsk (pendingAskObj: obj) =
        if isNull pendingAskObj || emitJsExpr pendingAskObj "$0 == null" then
            None
        else
            let q =
                getProp pendingAskObj [ "question"; "Question"; "pendingAskQuestion"; "PendingAskQuestion" ]

            let cur =
                getProp pendingAskObj [ "cursor"; "Cursor"; "pendingAskCursor"; "PendingAskCursor" ]

            let inc =
                getProp
                    pendingAskObj
                    [ "incumbencyId"
                      "IncumbencyId"
                      "pendingAskIncumbencyId"
                      "PendingAskIncumbencyId" ]

            extractPendingAskRecord q cur inc

    let private resolveAskWithPending
        (rawMessages: obj list)
        (pending:
            {| Question: string
               Cursor: int64
               IncumbencyId: string |})
        : obj =
        let parsed = rawMessages |> List.map messageOfRaw

        let userReplyIndex =
            List.zip rawMessages parsed |> List.tryFindIndex (isUserAnswer pending.Cursor)

        match userReplyIndex with
        | None ->
            box
                {| messages = rawMessages |> List.toArray
                   resolved = false
                   resolvedCursor = pending.Cursor
                   answer = null |}
        | Some idx ->
            let replyTm = List.item idx parsed
            let userText = replyTm.Content
            let ansCursor = defaultArg replyTm.Cursor (pending.Cursor + 1L)

            let toolResultMessage = createAskToolMessage replyTm

            let indexed =
                List.zip rawMessages parsed |> List.mapi (fun i (raw, tm) -> (i, raw, tm))

            let newMessages =
                indexed
                |> List.choose (filterOrReplaceMessage idx toolResultMessage)
                |> List.toArray

            box
                {| messages = newMessages
                   resolved = true
                   resolvedCursor = ansCursor
                   answer = userText |}

    let assembleAskContinuation (rawMessagesInput: obj) (pendingAskObj: obj) (tenureInput: obj) : obj =
        let rawMessages = parseRawMessages rawMessagesInput

        let pendingOpt = parsePendingAsk pendingAskObj

        match pendingOpt with
        | None ->
            box
                {| messages = rawMessages |> List.toArray
                   resolved = false
                   resolvedCursor = 0L
                   answer = null |}
        | Some pending -> resolveAskWithPending rawMessages pending
