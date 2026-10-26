namespace Wanxiangshu.Mission.Lifecycle

open System
open System.Threading.Tasks
open System.Collections.Generic
open Fable.Core
open Fable.Core.JsInterop
open Wanxiangshu.Host
open Wanxiangshu.Persistence.Journal

module LifecycleSurface =

    let observeDurable (requestId: string) (durableResult: obj) =
        let seen = DurableWorkHost.observe requestId durableResult

        box
            {| requestId = seen.RequestId
               status = seen.Status
               kind = seen.Kind
               reason = seen.Reason |}

    let mayStartDelivery (durableResult: obj) = DurableWorkHost.mayStart durableResult

    let journalPath (gitCommonDir: string) =
        DurableWorkHost.journalPath gitCommonDir

    let execute (gitCommonDir: string) (workId: string) (requestId: string) (command: obj) =
        DurableWorkHost.execute gitCommonDir workId requestId command

    let submitReview
        (gitCommonDir: string)
        (workId: string)
        (requestId: string)
        (office: string)
        (providerRunId: string)
        (toolCallId: string)
        (updates: obj)
        (blockers: obj)
        (digest: string)
        =
        DurableWorkHost.submitReview
            gitCommonDir
            workId
            requestId
            office
            providerRunId
            toolCallId
            updates
            blockers
            digest
            null

    let inspect (gitCommonDir: string) (workId: string) =
        DurableWorkHost.inspect gitCommonDir workId

    let inspected (gitCommonDir: string) (workId: string) =
        DurableWorkHost.inspected gitCommonDir workId

    [<Emit("JSON.parse($0)")>]
    let private parseJson (_text: string) : obj = jsNative

    [<Emit("Number.isSafeInteger(Number($0)) && Number($0) >= 0 ? Number($0) : Number.NaN")>]
    let private asNumber (_value: int64) : float = jsNative

    let private cursorOf (value: int64) : float option =
        let seen = asNumber value
        if Double.IsNaN seen then None else Some seen

    [<Emit("$0 != null && typeof $0 === 'object'")>]
    let private isRecord (_value: obj) : bool = jsNative

    let private remembered = Dictionary<string, string array>()

    let private recall (workId: string) =
        let found, records = remembered.TryGetValue workId
        if found then records else [||]

    let private remember (workId: string) (records: string array) =
        remembered.[workId] <- Array.copy records

    let private command (kind: string) (fields: (string * obj) list) =
        createObj (("type", box kind) :: fields)

    let private sequenceOf (kind: string) =
        invalidOp ("lifecycle sequence is retired; review is the only entry (" + kind + ")")

    let private recordsOf (text: string) =
        text.Split('\n') |> Array.filter (fun line -> line.Length > 0)

    let private workIdOf (records: string array) =
        if records.Length = 0 then
            "unread"
        else
            let events: obj array = unbox (parseJson records.[0])?events

            match events |> Array.tryFind (fun event -> string (event?``type``) = "WorkOpened") with
            | Some event -> string (event?workId)
            | None -> "unread"

    let private namesOfRecord (record: string) =
        if record.Length = 0 then
            ""
        else
            let events: obj array = unbox (parseJson record)?events
            events |> Array.map (fun event -> string (event?``type``)) |> String.concat ","

    let private joinRecords (records: string array) =
        if records.Length = 0 then
            ""
        else
            (records |> String.concat "\n") + "\n"

    let private useJournal (workId: string) (records: string array) (body: obj -> 'a) : 'a =
        DurableWorkHost.useEphemeralWork workId records body

    let private openCommand (root: string) (kind: string) (head: float) (ownerId: string) =
        command
            "Open"
            [ "root", box root
              "sequence", sequenceOf kind
              "ownerId", box ownerId
              "openingCursor", box head ]

    let private reviewCommand (incumbencyId: string) (outcome: string) (reviewerId: string) =
        let updates: obj =
            match outcome with
            | "Promoted" -> box ([||]: string array)
            | "Stayed" -> box [| createObj [ "key", box "stayed"; "value", box "stayed" ] |]
            | "Cleared" -> box [| createObj [ "key", box "stayed"; "value", null ] |]
            | _ -> null

        command
            "Review"
            [ "incumbencyId", box incumbencyId
              "reviewerId", box reviewerId
              "updates", updates ]

    let private deliverCommand
        (incumbencyId: string)
        (providerRunId: string)
        (toolCallId: string)
        (head: float)
        (draft: (string * string) option)
        =
        let draftText =
            match draft with
            | None -> ""
            | Some(digest, path) -> digest + " " + path

        invalidOp (
            "lifecycle deliver is retired; review is the only entry ("
            + incumbencyId
            + " "
            + providerRunId
            + " "
            + toolCallId
            + " "
            + string head
            + " "
            + draftText
            + ")"
        )

    let private lastOf (records: string array) =
        if records.Length = 0 then
            ""
        else
            records.[records.Length - 1]

    let private shape
        (failed: bool)
        (error: string)
        (status: string)
        (kind: string)
        (reason: string)
        (wrote: bool)
        (record: string)
        (authorized: bool)
        (authority: string)
        =
        if failed then
            box
                {| decision = "Refuse"
                   detail = error
                   names = ""
                   bytes = ""
                   authorized = authorized
                   authority = authority |}
        else
            match status, wrote with
            | "Applied", true
            | "Committed", true ->
                box
                    {| decision = "Write"
                       detail = kind
                       names = namesOfRecord record
                       bytes = record + "\n"
                       authorized = authorized
                       authority = authority |}
            | "Replayed", _ ->
                box
                    {| decision = "Done"
                       detail = status
                       names = ""
                       bytes = ""
                       authorized = authorized
                       authority = authority |}
            | "Observed", _ ->
                box
                    {| decision = "Done"
                       detail = kind
                       names = ""
                       bytes = ""
                       authorized = authorized
                       authority = authority |}
            | _ ->
                box
                    {| decision = "Refuse"
                       detail = if reason.Length = 0 then status else status + ": " + reason
                       names = ""
                       bytes = ""
                       authorized = authorized
                       authority = authority |}

    let private attempt (workId: string) (records: string array) (requestId: string) (command: obj) =
        try
            useJournal workId records (fun work ->
                let prior: obj = work?inspect ()

                let priorAuthority =
                    if isNull prior?authority then
                        ""
                    else
                        string prior?authority

                let authorizedPrior =
                    if isNull prior?deliveryAuthorized then
                        false
                    else
                        unbox<bool> prior?deliveryAuthorized

                let before: string array = unbox (work?records ())

                try
                    let result: obj = work?execute (requestId, command)
                    let after: string array = unbox (work?records ())
                    let snapshot: obj = work?inspect ()
                    let decision = result?result
                    let kind = if isRecord decision then string decision?kind else ""
                    let reason = if isNull result?reason then "" else string result?reason

                    shape
                        false
                        ""
                        (string result?status)
                        kind
                        reason
                        (after.Length > before.Length)
                        (lastOf after)
                        (unbox<bool> snapshot?deliveryAuthorized)
                        (if isNull snapshot?authority then
                             priorAuthority
                         else
                             string snapshot?authority)
                with error ->
                    shape true error.Message "" "" "" false "" authorizedPrior priorAuthority)
        with error ->
            shape true error.Message "" "" "" false "" false ""

    let encodeOpening (kind: string) (workId: string) (root: string) (head: int64) =
        sprintf "REFUSE the first review opens the work (%s %s %s %s)" kind workId root (string head)

    let private readProbeRecords (text: string) =
        try
            let records = recordsOf text
            let workId = workIdOf records

            useJournal workId records (fun work ->
                let snapshot: obj = work?inspect ()
                let recovery: obj = work?recover ()
                let history: obj array = unbox (work?history ())

                let names =
                    history
                    |> Array.collect (fun frame ->
                        let events: obj array = unbox frame?events
                        events |> Array.map (fun event -> string (event?``type``)))
                    |> String.concat ","

                let requestIds =
                    history |> Array.map (fun frame -> string frame?requestId) |> String.concat ","

                let sequence =
                    if isNull snapshot?sequence then
                        ""
                    else
                        let items: obj array = unbox snapshot?sequence
                        items |> Array.map string |> String.concat ","

                let active: obj = snapshot?active

                box
                    {| ok = true
                       next = string recovery?next
                       opened = unbox<bool> snapshot?opened
                       stage = if isNull snapshot?stage then "" else string snapshot?stage
                       sequence = sequence
                       names = names
                       revision = unbox<float> snapshot?revision
                       authorized = unbox<bool> snapshot?deliveryAuthorized
                       reason = ""
                       activeId = if isNull active then "" else string active?id
                       requestIds = requestIds |})
        with error ->
            box
                {| ok = false
                   next = ""
                   opened = false
                   stage = ""
                   sequence = ""
                   names = ""
                   revision = 0.
                   authorized = false
                   reason = error.Message
                   activeId = ""
                   requestIds = "" |}

    let readProbe (text: string) =
        if isNull text || text.StartsWith("REFUSE") || not (text.Contains "{") then
            box
                {| ok = false
                   next = ""
                   opened = false
                   stage = ""
                   sequence = ""
                   names = ""
                   revision = 0.
                   authorized = false
                   reason = if isNull text then "refused" else text
                   activeId = ""
                   requestIds = "" |}
        else
            readProbeRecords text

    let reviewProbe (text: string) (incumbencyId: string) (outcome: string) =
        box
            {| decision = "Refuse"
               detail = "review is the only lifecycle entry; " + outcome + " is not a command"
               names = ""
               bytes = ""
               authorized = false
               authority = "" |}

    let finalPromotionProbe (text: string) =
        box
            {| authorized = false
               detail = "review is the only lifecycle entry" |}

    let private supplied (draft: obj) =
        emitJsExpr draft "$0 != null && $0 !== undefined"

    let private draftPair (draft: obj) =
        if not (supplied draft) then
            None
        else
            Some(string draft?digest, string draft?path)

    /// The incumbency that a committed retirement fence belongs to. A replay
    /// of the same provider run and tool call must name that original tenure.
    let private completedIdOfFence (records: string array) (providerRunId: string) (toolCallId: string) =
        records
        |> Array.tryPick (fun record ->
            let events: obj array = unbox (parseJson record)?events

            events
            |> Array.tryPick (fun event ->
                if string (event?``type``) <> "IncumbencyRetired" then
                    None
                else
                    let cut = event?projectionCut
                    let fence = if isRecord cut then cut else event

                    if
                        string fence?providerRunId = providerRunId
                        && string fence?toolCallId = toolCallId
                    then
                        Some(string event?incumbencyId)
                    else
                        None))

    let private deliverAt (text: string) (providerRunId: string) (toolCallId: string) (cursor: float) (draft: obj) =
        let records = recordsOf text
        let workId = workIdOf records

        let incumbencyId =
            if records.Length = 0 then
                ""
            else
                try
                    match completedIdOfFence records providerRunId toolCallId with
                    | Some finished -> finished
                    | None ->
                        useJournal workId records (fun work ->
                            let snapshot: obj = work?inspect ()

                            if isNull snapshot?active then
                                ""
                            else
                                string snapshot?active?id)
                with _ ->
                    ""

        let selected = draftPair draft

        let requested =
            attempt
                workId
                records
                ("deliver:" + providerRunId + ":" + toolCallId)
                (deliverCommand incumbencyId providerRunId toolCallId cursor selected)

        if
            selected.IsNone
            || string requested?decision <> "Write"
            || string requested?detail <> "DeliveryRequested"
        then
            requested
        else
            let digest, path = selected.Value

            let operationId =
                let events: obj array = unbox (parseJson (string requested?bytes))?events
                string (events |> Array.find (fun event -> string event?``type`` = "DeliveryRequested"))?operationId

            let executionSession = "exec:" + operationId

            let bound =
                attempt
                    workId
                    (Array.append records (recordsOf (string requested?bytes)))
                    (operationId + ":session-created")
                    (command
                        "ResolveDelivery"
                        [ "operationId", box operationId
                          "outcome", box "SessionCreated"
                          "execSessionId", box executionSession ])

            let observed =
                if string bound?decision <> "Write" then
                    bound
                else
                    attempt
                        workId
                        (Array.append records (recordsOf (string requested?bytes + string bound?bytes)))
                        (operationId + ":accepted")
                        (command
                            "ResolveDelivery"
                            [ "operationId", box operationId
                              "outcome", box "Accepted"
                              "execSessionId", box executionSession
                              "digest", box digest
                              "path", box path
                              "blockers", box ([||]: string array) ])

            if string observed?decision <> "Write" then
                observed
            else
                box
                    {| decision = "Write"
                       detail = "delivered"
                       names = namesOfRecord (string observed?bytes)
                       bytes = string requested?bytes + string bound?bytes + string observed?bytes
                       authorized = unbox<bool> observed?authorized
                       authority = string observed?authority |}

    let deliverProbe (text: string) (providerRunId: string) (toolCallId: string) (head: int64) (draft: obj) =
        box
            {| decision = "Refuse"
               detail = "lifecycle deliver is retired; review is the only entry"
               names = ""
               bytes = ""
               authorized = false
               authority = "" |}

    let replayProbe (text: string) (providerRunId: string) (toolCallId: string) (head: int64) =
        deliverProbe text providerRunId toolCallId head null

    let deliverStateProbe (text: string) (providerRunId: string) (toolCallId: string) (head: int64) (draft: obj) =
        deliverProbe text providerRunId toolCallId head draft

    let replayStateProbe (text: string) (providerRunId: string) (toolCallId: string) (head: int64) (draft: obj) =
        let first = deliverProbe text providerRunId toolCallId head draft
        let written = string first?bytes
        let nextText = if written.Length = 0 then text else text + written
        let second = deliverProbe nextText providerRunId toolCallId head draft

        box
            {| decision = string second?decision
               detail = string second?detail
               names = string second?names
               bytes = string second?bytes
               original = string first?detail |}

    let private retirementIdOf (text: string) (incumbencyId: string) =
        recordsOf text
        |> Array.tryPick (fun record ->
            let events: obj array = unbox (parseJson record)?events

            events
            |> Array.tryPick (fun event ->
                if
                    string (event?``type``) = "IncumbencyRetired"
                    && string event?incumbencyId = incumbencyId
                then
                    Some(string event?retirementId)
                else
                    None))

    let reopenProbe (text: string) (incumbencyId: string) =
        box
            {| decision = "Refuse"
               detail = "a completed work reopens only from a new operator message"
               names = ""
               bytes = ""
               authorized = false
               authority = "" |}

    let commitProbe (name: string) =
        match name with
        | "Committed"
        | "Replayed"
        | "Conflict"
        | "NotWritten"
        | "Unknown" -> name
        | _ -> "not-durable"

    let private produceOpenReview (workId: string) =
        useJournal workId [||] (fun work ->
            let opened: obj =
                work?execute ("open:" + workId, openCommand "/repo" "plan" 7. "owner")

            ignore opened
            let snapshot: obj = work?inspect ()
            let id = string snapshot?active?id

            let reviewed: obj =
                work?execute ("review:" + id + ":Stayed", reviewCommand id "Stayed" "reviewer")

            ignore reviewed
            let records: string array = unbox (work?records ())
            Array.copy records)

    let advance (workId: string) (prior: string) (expected: string) (next: string) =
        try
            let records = Array.append (produceOpenReview workId) (recordsOf prior)

            useJournal workId records (fun work ->
                let priorSnap: obj = work?inspect ()

                let authority =
                    if isNull priorSnap?authority then
                        ""
                    else
                        string priorSnap?authority

                let active: obj = priorSnap?active

                if isNull active then
                    box
                        {| decision = "Refuse"
                           detail = "no active incumbency"
                           names = ""
                           bytes = ""
                           authorized = false
                           authority = authority |}
                else
                    let advanceCommand =
                        command
                            "Advance"
                            [ "incumbencyId", box (string active?id)
                              "expected", box expected
                              "next", box next ]

                    try
                        let result: obj = work?execute ("advance:" + expected + ":" + next, advanceCommand)
                        let after: string array = unbox (work?records ())
                        let snapshot: obj = work?inspect ()
                        let decision = result?result
                        let kind = if isRecord decision then string decision?kind else ""
                        let status = string result?status
                        let record = lastOf after

                        if status = "Committed" then
                            box
                                {| decision = "Write"
                                   detail = kind
                                   names = namesOfRecord record
                                   bytes = record + "\n"
                                   authorized = unbox<bool> snapshot?deliveryAuthorized
                                   authority = string snapshot?authority |}
                        else
                            box
                                {| decision = "Done"
                                   detail = kind
                                   names = ""
                                   bytes = ""
                                   authorized = unbox<bool> snapshot?deliveryAuthorized
                                   authority = authority |}
                    with error ->
                        box
                            {| decision = "Refuse"
                               detail = error.Message
                               names = ""
                               bytes = ""
                               authorized = false
                               authority = authority |})
        with error ->
            box
                {| decision = "Refuse"
                   detail = error.Message
                   names = ""
                   bytes = ""
                   authorized = false
                   authority = "" |}

    let private runSeed (workId: string) (requestId: string) (issued: obj) =
        try
            Ok(
                useJournal workId (recall workId) (fun work ->
                    let before: string array = unbox (work?records ())
                    let result: obj = work?execute (requestId, issued)
                    let after: string array = unbox (work?records ())
                    remember workId after
                    result, after.Length > before.Length)
            )
        with error ->
            Error error.Message

    let private stampedDir (store: obj) : string option =
        let raw =
            emitJsExpr store "$0 && typeof $0.gitCommonDir === 'string' ? $0.gitCommonDir : ''"

        let text = if isNull raw then "" else string raw
        if String.IsNullOrWhiteSpace text then None else Some text

    let private seedOpenAt
        (store: obj)
        (kind: string)
        (workId: string)
        (root: string)
        (cursor: float)
        (ownerId: string)
        : Task<obj> =
        task {
            match stampedDir store with
            | Some dir ->
                try
                    let issued = openCommand root kind cursor ownerId
                    let receipt = DurableWorkHost.execute dir workId ("open:" + workId) issued

                    if receipt.Status = "Committed" || receipt.Status = "Replayed" then
                        return
                            box
                                {| ok = true
                                   opened = true
                                   error = "" |}
                    else
                        return
                            box
                                {| ok = false
                                   opened = false
                                   error =
                                    if receipt.Reason = "" then
                                        receipt.Status
                                    else
                                        receipt.Reason |}
                with error ->
                    return
                        box
                            {| ok = false
                               opened = false
                               error = error.Message |}
            | None ->
                if (recall workId).Length > 0 then
                    return
                        box
                            {| ok = true
                               opened = false
                               error = "" |}
                else
                    match runSeed workId ("open:" + workId) (openCommand root kind cursor ownerId) with
                    | Ok(result, wrote) ->
                        let status = string result?status

                        return
                            box
                                {| ok = status = "Committed" || status = "Replayed"
                                   opened = wrote
                                   error = "" |}
                    | Error reason ->
                        return
                            box
                                {| ok = false
                                   opened = false
                                   error = reason |}
        }

    let private seedOpenWith
        (_store: obj)
        (_kind: string)
        (_workId: string)
        (_root: string)
        (_head: int64)
        (_ownerId: string)
        : Task<obj> =
        Task.FromResult(
            box
                {| ok = false
                   opened = false
                   error = "the first review opens the work" |}
        )

    let seedOpen (store: obj) (kind: string) (workId: string) (root: string) (head: int64) : Task<obj> =
        seedOpenWith store kind workId root head "owner"

    let seedOpenAs
        (store: obj)
        (kind: string)
        (workId: string)
        (root: string)
        (head: int64)
        (ownerId: string)
        : Task<obj> =
        seedOpenWith store kind workId root head ownerId

    let private seedReviewWith (_store: obj) (_workId: string) (outcome: string) (_reviewerId: string) : Task<obj> =
        Task.FromResult(
            box
                {| ok = false
                   reviewed = false
                   error = "review is the only lifecycle entry; " + outcome + " is not a command" |}
        )

    let seedReview (store: obj) (workId: string) (outcome: string) : Task<obj> =
        seedReviewWith store workId outcome "reviewer"

    let seedReviewAs (store: obj) (workId: string) (outcome: string) (reviewerId: string) : Task<obj> =
        seedReviewWith store workId outcome reviewerId

    let private normalizedDecision (kind: string) =
        match kind with
        | "Relayed" -> "relayed"
        | "Delivered" -> "delivered"
        | _ -> kind

    /// interaction-authority-018: a completed manager life closes its authority
    /// run. The milestone 「已完成」 and Delivered are that fact. The closure id
    /// is the Completed retirement, not an S4 sequence.
    let private completedManagerDelivery (dir: string) (workId: string) =
        match DurableWorkHost.inspected dir workId with
        | Some work -> DurableWorkHost.completedClosureId work
        | None -> None

    let private seedDeliverAt
        (store: obj)
        (workId: string)
        (providerRunId: string)
        (toolCallId: string)
        (cursor: float)
        : Task<obj> =
        task {
            match stampedDir store with
            | Some dir ->
                try
                    match DurableWorkHost.inspected dir workId with
                    | None ->
                        return
                            box
                                {| ok = false
                                   decision = "missing"
                                   error = "lifecycle work is missing" |}
                    | Some work ->
                        match completedManagerDelivery dir workId with
                        | Some retirementId ->
                            // WHAT-018: the durable source terminal already exists, so a
                            // retry may only repeat the idempotent closure append; the
                            // delivery command would carry a different payload for the
                            // same request id once the tenure is retired.
                            do! JournalSurface.appendAuthorityLogicalRunClosed dir workId retirementId

                            return
                                box
                                    {| ok = true
                                       decision = "delivered"
                                       error = "" |}
                        | None ->
                            let incumbencyId = work.ActiveId |> Option.defaultValue ""

                            let receipt =
                                DurableWorkHost.execute
                                    dir
                                    workId
                                    ("deliver:" + providerRunId + ":" + toolCallId)
                                    (deliverCommand incumbencyId providerRunId toolCallId cursor None)

                            let kind =
                                if receipt.Kind.Length = 0 then
                                    receipt.Status
                                else
                                    receipt.Kind

                            if
                                receipt.Status = "Committed"
                                || receipt.Status = "Replayed"
                                || receipt.Status = "Observed"
                            then
                                match completedManagerDelivery dir workId with
                                | Some retirementId ->
                                    do! JournalSurface.appendAuthorityLogicalRunClosed dir workId retirementId
                                | None -> ()

                                return
                                    box
                                        {| ok = true
                                           decision = normalizedDecision kind
                                           error = "" |}
                            else
                                return
                                    box
                                        {| ok = false
                                           decision = "Refuse"
                                           error =
                                            if receipt.Reason = "" then
                                                receipt.Status
                                            else
                                                receipt.Reason |}
                with error ->
                    return
                        box
                            {| ok = false
                               decision = "Refuse"
                               error = error.Message |}
            | None ->
                if (recall workId).Length = 0 then
                    return
                        box
                            {| ok = false
                               decision = "missing"
                               error = "lifecycle work is missing" |}
                else
                    let incumbencyId =
                        try
                            useJournal workId (recall workId) (fun work ->
                                let snapshot: obj = work?inspect ()

                                if isNull snapshot?active then
                                    ""
                                else
                                    string snapshot?active?id)
                        with error ->
                            error.Message

                    match
                        runSeed
                            workId
                            ("deliver:" + providerRunId + ":" + toolCallId)
                            (deliverCommand incumbencyId providerRunId toolCallId cursor None)
                    with
                    | Ok(result, _) ->
                        let status = string result?status
                        let decision = result?result
                        let kind = if isRecord decision then string decision?kind else status

                        return
                            box
                                {| ok = status = "Committed" || status = "Replayed" || status = "Observed"
                                   decision = normalizedDecision kind
                                   error = "" |}
                    | Error reason ->
                        return
                            box
                                {| ok = false
                                   decision = "Refuse"
                                   error = reason |}
        }

    let seedDeliver
        (store: obj)
        (workId: string)
        (providerRunId: string)
        (toolCallId: string)
        (head: int64)
        : Task<obj> =
        task {
            match cursorOf head with
            | None ->
                return
                    box
                        {| ok = false
                           decision = "Refuse"
                           error = "cursor out of range" |}
            | Some cursor -> return! seedDeliverAt store workId providerRunId toolCallId cursor
        }

    let private roundRange (round: obj) =
        string round?openingCursor + "," + string round?retirementCursor

    /// A reopened incumbency keeps the range inherited when its delivered tenure
    /// first opened; its own delivered retirement is not the predecessor.
    let private reopenedIncumbency (history: obj array) =
        let events = history |> Array.collect (fun frame -> unbox<obj array> frame?events)
        // DSL-MUTABLE: algorithm-scratch — index of the last IncumbencyOpened while scanning
        let mutable lastOpening = -1

        for index in 0 .. events.Length - 1 do
            if string (events.[index])?``type`` = "IncumbencyOpened" then
                lastOpening <- index

        lastOpening >= 1 && string (events.[lastOpening - 1])?``type`` = "Reopened"

    let documentView (text: string) =
        let records = recordsOf text
        let workId = workIdOf records

        try
            useJournal workId records (fun work ->
                let snapshot: obj = work?inspect ()
                let recovery: obj = work?recover ()

                if not (unbox<bool> snapshot?opened) then
                    box
                        {| found = false
                           detail = "not opened" |}
                else
                    let active: obj = snapshot?active

                    let rounds: obj array =
                        if isNull snapshot?rounds then
                            [||]
                        else
                            unbox snapshot?rounds

                    let history: obj array = unbox (work?history ())
                    let reopened = reopenedIncumbency history
                    // DSL-MUTABLE: algorithm-scratch — last Delivered draft digest while scanning
                    let mutable digest = ""
                    // DSL-MUTABLE: algorithm-scratch — last Delivered draft path while scanning
                    let mutable path = ""
                    // DSL-MUTABLE: algorithm-scratch — last Delivered exec session while scanning
                    let mutable execSessionId = ""

                    for frame in history do
                        let events: obj array = unbox frame?events

                        for event in events do
                            if
                                string (event?``type``) = "IncumbencyRetired"
                                && string event?outcome = "Delivered"
                            then
                                if not (isNull event?digest) then
                                    digest <- string event?digest

                                if not (isNull event?path) then
                                    path <- string event?path

                                if not (isNull event?execSessionId) then
                                    execSessionId <- string event?execSessionId

                    let predecessor =
                        if isNull active then
                            ""
                        elif reopened && rounds.Length >= 2 then
                            roundRange rounds.[rounds.Length - 2]
                        elif rounds.Length > 0 then
                            roundRange rounds.[rounds.Length - 1]
                        else
                            ""

                    let latest =
                        if rounds.Length = 0 then
                            ""
                        else
                            roundRange rounds.[rounds.Length - 1]

                    box
                        {| found = true
                           detail = ""
                           workId = string snapshot?workId
                           root = string snapshot?root
                           projection = string snapshot?stage
                           authorized = unbox<bool> snapshot?deliveryAuthorized
                           active = not (isNull active)
                           activeId = if isNull active then "" else string active?id
                           openingStage = if isNull active then "" else string active?openingStage
                           openingCursor =
                            if isNull active then
                                0.
                            else
                                unbox<float> active?openingCursor
                           retiredCount = rounds.Length
                           delivered = isNull active && string recovery?next = "Done"
                           digest = digest
                           path = path
                           execSessionId = execSessionId
                           devopsId =
                            if isNull snapshot?devopsId then
                                ""
                            else
                                string snapshot?devopsId
                           predecessor = predecessor
                           latestRetirement = latest |})
        with error ->
            box
                {| found = false
                   detail = error.Message |}

    let private emptyWorkView () =
        box
            {| found = false
               sequence = ""
               active = false
               reviewed = ""
               delivered = false |}

    let private presentWorkView (work: DurableWorkHost.InspectedWork) =
        box
            {| found = true
               sequence = String.concat "," work.Sequence
               active = work.ActiveId.IsSome
               reviewed = work.ReviewOutcome |> Option.defaultValue ""
               delivered = work.Delivered |}

    let private namesOfEnvelope (line: string) =
        let value = parseJson line
        let events = value?events
        let kind = value?event_type

        if not (isNull events) then
            unbox<obj array> events |> Array.map (fun event -> string (event?``type``))
        elif not (isNull kind) then
            [| string kind |]
        else
            failwith "line is missing an event envelope"

    let decodeDocument (text: string) =
        if text.Length = 0 || not (text.EndsWith "\n") then
            box
                {| ok = false
                   names = ""
                   fault = "lifecycle document does not end with a newline" |}
        else
            try
                let names = recordsOf text |> Array.collect namesOfEnvelope |> String.concat ","

                box
                    {| ok = true
                       names = names
                       fault = "" |}
            with error ->
                box
                    {| ok = false
                       names = ""
                       fault = error.Message |}

    let detachedOpeningProbe (text: string) =
        let decoded = decodeDocument text

        let detail =
            if unbox<bool> (decoded?ok) then
                "no legal incumbency opening"
            else
                string (decoded?fault)

        box
            {| decision = "Refuse"
               detail = detail
               names = "" |}

    let workView (store: obj) (workId: string) =
        match stampedDir store with
        | None -> emptyWorkView ()
        | Some dir ->
            DurableWorkHost.inspected dir workId
            |> Option.map presentWorkView
            |> Option.defaultWith emptyWorkView

    let private bindReceipt
        (dir: string)
        (workId: string)
        (work: DurableWorkHost.InspectedWork)
        (devopsId: string)
        (target: obj)
        =
        let incumbencyId = work.ActiveId |> Option.defaultValue ""
        let targetValue: obj = if isNull target then null else box (string target)

        let command =
            DurableWorkHost.command
                "BindDevOps"
                [ "incumbencyId", box incumbencyId
                  "trigger", box "Ask"
                  "devopsId", box devopsId
                  "target", targetValue ]

        let receipt =
            DurableWorkHost.execute dir workId (workId + ":bind:" + incumbencyId) command

        if
            receipt.Status = "Committed"
            || receipt.Status = "Replayed"
            || receipt.Status = "Observed"
        then
            box {| ok = true; error = "" |}
        else
            box
                {| ok = false
                   error =
                    (if receipt.Reason = "" then
                         receipt.Status
                     else
                         receipt.Reason) |}

    let bindDevOps (store: obj) (workId: string) (devopsId: string) (target: obj) : Task<obj> =
        task {
            let dir = stampedDir store

            let inspected =
                dir |> Option.bind (fun common -> DurableWorkHost.inspected common workId)

            match dir, inspected with
            | Some common, Some work when work.DevOpsId.IsNone -> return bindReceipt common workId work devopsId target
            | Some _, Some _ ->
                // First binding wins; a repeated binding writes nothing and keeps the stored target.
                return box {| ok = true; error = "" |}
            | _ ->
                return
                    box
                        {| ok = false
                           error = "lifecycle work is missing" |}
        }

    let private lastDevOpsTarget (work: obj) : string option =
        let frames: obj array = unbox (work?history ())

        frames
        |> Array.collect (fun frame -> unbox<obj array> frame?events)
        |> Array.choose (fun event ->
            let target = event?target

            if string (event?``type``) <> "DevOpsBound" || isNull target then
                None
            else
                Some(string target))
        |> Array.tryLast

    let workDevOps (store: obj) (workId: string) =
        match stampedDir store with
        | None -> null
        | Some dir ->
            DurableWorkHost.inspected dir workId
            |> Option.bind (fun work -> work.DevOpsId)
            |> Option.map (fun devopsId ->
                let target = DurableWorkHost.useWork dir workId (fun live -> lastDevOpsTarget live)

                box
                    {| devopsId = devopsId
                       modelTarget = target |> Option.toObj |})
            |> Option.defaultValue null

    let executeOwnershipDurable
        (gitCommonDir: string)
        (registryId: string)
        (requestId: string)
        (message: obj)
        : DurableWorkHost.HostReceipt =
        DurableWorkHost.executeOwnershipDurable gitCommonDir registryId requestId message

    let mayStartResource (receipt: DurableWorkHost.HostReceipt) : DurableWorkHost.OwnershipLease option =
        DurableWorkHost.mayStartResource receipt

    let ownershipBlockersDurable
        (gitCommonDir: string)
        (registryId: string)
        (scopeId: string)
        : DurableWorkHost.OwnershipBlocker array =
        DurableWorkHost.ownershipBlockersDurable gitCommonDir registryId scopeId
        |> List.toArray
