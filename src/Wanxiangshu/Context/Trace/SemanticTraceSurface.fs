namespace Wanxiangshu.Context.Trace

open Wanxiangshu.Persistence.Journal.JournalOutcome
open System
open System.Threading.Tasks
open Fable.Core
open Fable.Core.JsInterop
open Wanxiangshu.Composition.Durable
open Wanxiangshu.Context.Companion
open Wanxiangshu.Foundation.Identity
open Wanxiangshu.Interaction.Authority
open Wanxiangshu.Participant.Provider.Projection.ProviderProjection
open Wanxiangshu.Persistence.Journal
open Wanxiangshu.Foundation

/// JS proof surface for semantic-trace owner operations.
/// Typed owner state never crosses this boundary; queries return copied plain evidence.
[<RequireQualifiedAccess>]
module SemanticTraceSurface =

    /// Opaque proof capability for the semantic-trace owner.
    type SemanticTraceProjection private (state: XTraceProjectionState) =
        member internal _.State = state
        static member internal Create(state: XTraceProjectionState) = SemanticTraceProjection state

    [<Emit("$0 == null")>]
    let private isNullish (value: obj) : bool = jsNative

    let private field (value: obj) (name: string) : obj =
        if isNullish value then
            null
        else
            emitJsExpr (value, name) "$0[$1]"

    let private text (value: obj) =
        if isNullish value then "" else string value

    let private optionalText value =
        if isNullish value then None else Some(text value)

    let private arrayOf value =
        if isNullish value then [||] else unbox<obj array> value

    let private cursorOf value =
        XTraceCursor.create (int64 (text (field value "sequence")))

    let private cursorView cursor : obj =
        box {| sequence = XTraceCursor.sequence cursor |> int |}

    let private rangeOf value =
        XTraceRange.create (cursorOf (field value "start")) (cursorOf (field value "endExclusive"))

    let private rangeView range : obj =
        box
            {| start = XTraceRange.startInclusive range |> cursorView
               endExclusive = XTraceRange.endExclusive range |> cursorView |}

    let private cutView (cut: XTraceCut) : obj =
        box
            {| traceId = cut.TraceId
               cursor = cut.Cursor
               digest = cut.Digest |}

    let private semanticPartView part : obj =
        match part with
        | SemanticText value -> box {| kind = "text"; text = value |}
        | SemanticReasoning value -> box {| kind = "reasoning"; text = value |}
        | SemanticToolCall(name, args) ->
            box
                {| kind = "tool-call"
                   name = name
                   args = args |}
        | SemanticToolResult value ->
            box
                {| kind = "tool-result"
                   result = value |}
        | SemanticMedia(mediaType, digest) ->
            box
                {| kind = "media"
                   mediaType = mediaType |> Option.map box |> Option.toObj
                   digest = digest |}

    let private semanticPartOf value =
        match text (field value "kind") with
        | "text" -> SemanticText(text (field value "text"))
        | "reasoning" -> SemanticReasoning(text (field value "text"))
        | "tool-call"
        | "tool_call" -> SemanticToolCall(text (field value "name"), text (field value "args"))
        | "tool-result"
        | "tool_result" -> SemanticToolResult(text (field value "result"))
        | "media" -> SemanticMedia(optionalText (field value "mediaType"), text (field value "digest"))
        | other -> failwith $"SemanticTraceSurface: unknown semantic part '{other}'"

    let private messagePartOf value =
        match text (field value "kind") with
        | "text" -> Wanxiangshu.OpenCode.MessagePart.Text(text (field value "text"))
        | "reasoning" -> Wanxiangshu.OpenCode.MessagePart.Reasoning(text (field value "text"))
        | "tool-call"
        | "tool_call" ->
            Wanxiangshu.OpenCode.MessagePart.ToolCall(
                text (field value "callId"),
                text (field value "name"),
                text (field value "args")
            )
        | "tool-result"
        | "tool_result" ->
            Wanxiangshu.OpenCode.MessagePart.ToolResult(text (field value "callId"), text (field value "result"))
        | "activity" -> Wanxiangshu.OpenCode.MessagePart.Activity(text (field value "activity"))
        | other -> failwith $"SemanticTraceSurface: unknown capture part '{other}'"

    let private projectionOf value : ProviderSemanticProjection =
        let messages =
            arrayOf (field value "messages")
            |> Array.map (fun message ->
                { Role = text (field message "role")
                  Parts = arrayOf (field message "parts") |> Array.map semanticPartOf |> Array.toList })
            |> Array.toList

        { ProviderId = None
          ModelId = None
          Variant = None
          Tools = []
          System = []
          Messages = messages }

    let private projectionView (projection: ProviderSemanticProjection) : obj =
        box
            {| messages =
                projection.Messages
                |> List.map (fun message ->
                    box
                        {| role = message.Role
                           parts = message.Parts |> List.map semanticPartView |> List.toArray |})
                |> List.toArray |}

    let private partView (part: XTraceSemanticPartView) : obj =
        box
            {| cursor = cursorView part.Cursor
               provenance = part.Provenance
               generation = part.Generation
               role = part.Role
               turn = part.Turn
               partIndex = part.PartIndex
               kind = part.Kind
               toolName = part.ToolName |> Option.map box |> Option.toObj
               providerRun =
                part.ProviderRun
                |> Option.map (ProviderRunIdentity.value >> box)
                |> Option.toObj
               toolCallId = part.ToolCallId |> Option.map (ToolCallId.value >> box) |> Option.toObj
               hostToolPartId = part.HostToolPartId |> Option.map (HostToolPartId.value >> box) |> Option.toObj
               textRef = BlobRef.value part.TextRef
               textDigest = BlobDigest.value part.TextDigest |}

    let private openingView (opening: XTraceOpeningEvidence) : obj =
        box
            {| assignmentText = opening.AssignmentText
               authoritativeRequirements = opening.AuthoritativeRequirements |> List.toArray
               constitutiveBody = opening.ConstitutiveBody |}

    let private terminalView (terminal: XTraceTerminalEvidence) : obj =
        box
            {| textRef = BlobRef.value terminal.TextRef
               textDigest = BlobDigest.value terminal.TextDigest
               providerRun = ProviderRunIdentity.value terminal.ProviderRun
               frontier = cursorView terminal.Frontier |}

    let private traceOf (handle: JournalHandle) sessionId =
        AgentJournal.snapshot handle.Journal
        |> fun snapshot -> AgentProjection.tryFind (SessionId.create sessionId) snapshot.AgentProjections
        |> Option.bind (fun session -> session.XTrace)
        |> Option.defaultValue XTraceProjection.empty
        |> SemanticTraceProjection.Create

    let private rejection error =
        match error with
        | XTraceFoldRejection.OpeningAlreadyCaptured -> "opening-already-captured"
        | XTraceFoldRejection.CursorNotAfterHead(expected, actual) -> $"cursor-{actual}-not-after-{expected}"
        | XTraceFoldRejection.TerminalAlreadyCaptured -> "terminal-already-captured"

    let private updateResult result : obj =
        match result with
        | Ok state ->
            box
                {| ok = true
                   projection = SemanticTraceProjection.Create state |}
        | Error error ->
            box
                {| ok = false
                   error = rejection error |}

    let private captureError error =
        match error with
        | XTraceCaptureError.Refused reason -> reason
        | XTraceCaptureError.StorageAppendFailed failure -> JournalAppendFailure.describe failure
        | XTraceCaptureError.StorageFailed reason -> reason

    let private identityView identity =
        match identity with
        | XTraceCaptureIdentity.NoDurableTrace -> "no-durable-trace"
        | XTraceCaptureIdentity.PositionalIdentity -> "positional"
        | XTraceCaptureIdentity.StableHostIdentity -> "stable-host"

    let private receiptView result : obj =
        match result with
        | Error error ->
            box
                {| ok = false
                   error = captureError error |}
        | Ok receipt ->
            box
                {| ok = true
                   previousHead = cursorView receipt.PreviousHead
                   currentHead = cursorView receipt.CurrentHead
                   capturedPartCount = receipt.CapturedPartCount
                   openingCaptured = receipt.OpeningCaptured
                   terminalCaptured = receipt.TerminalCaptured
                   identity = identityView receipt.Identity |}

    let textPart value : obj = box {| kind = "text"; text = value |}

    let reasoningPart value : obj =
        box {| kind = "reasoning"; text = value |}

    let toolCallPart callId name args : obj =
        box
            {| kind = "tool-call"
               callId = callId
               name = name
               args = args |}

    let toolResultPart callId result : obj =
        box
            {| kind = "tool-result"
               callId = callId
               result = result |}

    let activityPart kind : obj =
        box {| kind = "activity"; activity = kind |}

    let mapPart value : obj option =
        value
        |> messagePartOf
        |> XTraceCapture.semanticPart
        |> Option.map semanticPartView

    let semanticText value : obj = semanticPartView (SemanticText value)

    let semanticReasoning value : obj =
        semanticPartView (SemanticReasoning value)

    let semanticToolCall name args : obj =
        semanticPartView (SemanticToolCall(name, args))

    let semanticToolResult value : obj =
        semanticPartView (SemanticToolResult value)

    let semanticMedia mediaType digest : obj =
        semanticPartView (SemanticMedia(optionalText mediaType, digest))

    let emptyProjection () =
        SemanticTraceProjection.Create XTraceProjection.empty

    let private stateOf (projection: SemanticTraceProjection) : XTraceProjectionState = projection.State

    let appendOpening (projection: SemanticTraceProjection) assignmentText (requirements: string array) : obj =
        XTraceProjection.applyOpening assignmentText (requirements |> Array.toList) (stateOf projection)
        |> updateResult

    let appendPart (projection: SemanticTraceProjection) value : obj =
        XTraceProjection.applyPart
            (int64 (text (field value "sequence")))
            (text (field value "role"))
            (text (field value "provenance"))
            (int (text (field value "turn")))
            (int (text (field value "partIndex")))
            (text (field value "kind"))
            (optionalText (field value "toolName"))
            (optionalText (field value "providerRun")
             |> Option.map ProviderRunIdentity.create)
            (optionalText (field value "toolCallId") |> Option.map ToolCallId.create)
            (optionalText (field value "hostToolPartId") |> Option.map HostToolPartId.create)
            (BlobRef.create (text (field value "textRef")))
            (BlobDigest.create (text (field value "textDigest")))
            (stateOf projection)
        |> updateResult

    let appendTerminal (projection: SemanticTraceProjection) value : obj =
        XTraceProjection.applyTerminal
            (BlobRef.create (text (field value "textRef")))
            (BlobDigest.create (text (field value "textDigest")))
            (ProviderRunIdentity.create (text (field value "providerRun")))
            (stateOf projection)
        |> updateResult

    let openingEvidence (projection: SemanticTraceProjection) : obj option =
        XTraceProjection.openingEvidence (stateOf projection) |> Option.map openingView

    let hasOpening projection =
        XTraceProjection.hasOpening (stateOf projection)

    let hasSemanticParts projection =
        XTraceProjection.hasSemanticParts (Some(stateOf projection))

    let orderedSemanticParts projection =
        XTraceProjection.orderedSemanticParts (stateOf projection)
        |> List.map partView
        |> List.toArray

    let currentGenerationSemanticParts projection =
        XTraceProjection.currentGenerationSemanticParts (stateOf projection)
        |> List.map partView
        |> List.toArray

    let partKinds projection =
        XTraceProjection.partKinds (stateOf projection) |> List.toArray

    let latestPartCursor projection =
        XTraceProjection.latestPartCursor (stateOf projection) |> Option.map cursorView

    let headCursor projection =
        XTraceProjection.headCursor (stateOf projection) |> cursorView

    let frontierAfter value =
        field value "cursor" |> cursorOf |> XTraceCursor.nextCursor |> cursorView

    let rangeFrom start projection =
        XTraceProjection.rangeFrom (cursorOf start) (stateOf projection) |> rangeView

    let rangeOfPart value =
        let cursor = cursorOf (field value "cursor")
        XTraceRange.create cursor (XTraceCursor.nextCursor cursor) |> rangeView

    let slice range projection =
        XTraceProjection.slice (rangeOf range) (stateOf projection)
        |> List.map partView
        |> List.toArray

    let traceHeadCut projection : obj =
        XTraceProjection.traceHeadCut (stateOf projection) |> cutView

    let traceCutAt cursor projection : obj =
        XTraceProjection.traceCutAt (cursorOf cursor) (stateOf projection) |> cutView

    let traceIntervalOfRange range projection : obj =
        let afterCut, throughCut =
            XTraceProjection.traceIntervalOfRange (rangeOf range) (stateOf projection)

        box
            {| after = cutView afterCut
               through = cutView throughCut |}

    let traceIntervalAfterCoverage cursor projection : obj =
        let coverage = RecordCoverage.create (cursorOf cursor)

        let afterCut, throughCut =
            XTraceProjection.traceIntervalAfterCoverage coverage (stateOf projection)

        box
            {| after = cutView afterCut
               through = cutView throughCut |}

    let traceRecords projection : string array =
        XTraceProjection.traceRecords (stateOf projection) |> List.toArray

    let latestTerminalEvidence projection =
        XTraceProjection.latestTerminalEvidence (stateOf projection)
        |> Option.map terminalView

    let terminalEvidenceForProviderRun providerRun projection =
        XTraceProjection.terminalEvidenceForProviderRun (ProviderRunIdentity.create providerRun) (stateOf projection)
        |> Option.map terminalView

    let providerRunParts providerRun projection =
        XTraceProjection.providerRunParts (ProviderRunIdentity.create providerRun) (stateOf projection)
        |> List.map partView
        |> List.toArray

    let toolResultParts providerRun toolCallId projection =
        XTraceProjection.toolResultParts
            (ProviderRunIdentity.create providerRun)
            (ToolCallId.create toolCallId)
            (stateOf projection)
        |> List.map partView
        |> List.toArray

    let toolPartsForHostIdentity providerRun toolCallId hostToolPartId projection =
        XTraceProjection.toolPartsForHostIdentity
            (ProviderRunIdentity.create providerRun)
            (ToolCallId.create toolCallId)
            (HostToolPartId.create hostToolPartId)
            (stateOf projection)
        |> List.map partView
        |> List.toArray

    let tryHostMessageIdAt cursor projection =
        XTraceProjection.tryHostMessageIdAt (cursorOf cursor) (stateOf projection)

    let hostMessageFirstCursors projection : obj =
        XTraceProjection.hostMessageFirstCursors (stateOf projection)
        |> Map.toList
        |> List.map (fun (messageId, cursor) -> messageId, box cursor)
        |> createObj

    let partsForHostMessageIds ids projection =
        XTraceProjection.partsForHostMessageIds (Set.ofArray ids) (stateOf projection)
        |> List.map partView
        |> List.toArray

    let tryContiguousHostRange ids projection =
        XTraceProjection.tryContiguousHostRange (Set.ofArray ids) (stateOf projection)
        |> Option.map rangeView

    let tryTurnOfHostMessageId id projection =
        XTraceProjection.tryTurnOfHostMessageId id (stateOf projection)

    let tryOpeningHostMessageId projection =
        XTraceProjection.tryOpeningHostMessageId (stateOf projection)

    let hostMessageIdsBeforeTurn cutoff projection =
        XTraceProjection.hostMessageIdsBeforeTurn cutoff (stateOf projection)
        |> List.toArray

    let semanticCursorAfter cursor projection : obj =
        let afterCursor =
            XTraceProjection.semanticCursorAfter (cursorOf cursor) (stateOf projection)

        let state = stateOf projection
        let currentParts = XTraceProjection.currentGenerationSemanticParts state

        match currentParts |> List.tryFind (fun part -> part.Cursor = afterCursor) with
        | Some part ->
            box
                {| turn = part.Turn
                   partIndex = part.PartIndex |}
        | None ->
            match List.tryLast currentParts with
            | Some last ->
                box
                    {| turn = last.Turn + 1
                       partIndex = 0 |}
            | None -> box {| turn = 0; partIndex = 0 |}

    let originCursor: obj = XTraceCursor.originCursor |> cursorView

    let cursor sequence : obj =
        XTraceCursor.create (int64 sequence) |> cursorView

    let next value =
        XTraceCursor.nextCursor (cursorOf value) |> cursorView

    let isAfter value previous =
        XTraceCursor.isAfter (cursorOf value) (cursorOf previous)

    let isAtOrAfter value previous =
        XTraceCursor.isAtOrAfter (cursorOf value) (cursorOf previous)

    let isBefore value nextValue =
        XTraceCursor.isBefore (cursorOf value) (cursorOf nextValue)

    let createRange start endExclusive =
        XTraceRange.create (cursorOf start) (cursorOf endExclusive) |> rangeView

    let rangeContains value range =
        XTraceRange.contains (cursorOf value) (rangeOf range)

    let rangeIsEmpty range = XTraceRange.isEmpty (rangeOf range)

    let private itemOf value : XTraceItem =
        let cursorValue = field value "cursor"

        let cursor =
            if isNullish cursorValue then
                XTraceCursor.create (int64 (text (field value "sequence")))
            else
                cursorOf cursorValue

        { Cursor = cursor
          Provenance = text (field value "provenance")
          Role = text (field value "role")
          Part = semanticPartOf (field value "part") }

    let private itemView (value: XTraceItem) : obj =
        box
            {| cursor = cursorView value.Cursor
               provenance = value.Provenance
               role = value.Role
               part = semanticPartView value.Part |}

    let item value = value |> itemOf |> itemView

    let sliceBetween start endExclusive values =
        arrayOf values
        |> Array.toList
        |> List.map itemOf
        |> XTrace.sliceBetween (cursorOf start) (cursorOf endExclusive)
        |> List.map itemView
        |> List.toArray

    let sliceFrom start values =
        arrayOf values
        |> Array.toList
        |> List.map itemOf
        |> XTrace.sliceFrom (cursorOf start)
        |> List.map itemView
        |> List.toArray

    let inspectBridge (traceId: string) openingText range values : obj =
        let items = arrayOf values |> Array.toList |> List.map itemOf
        WanxiangliTrace.inspectItemsBridge traceId (optionalText openingText) (rangeOf range) items

    let forOpening values =
        arrayOf values
        |> Array.toList
        |> List.map itemOf
        |> XTrace.forOpening
        |> List.map itemView
        |> List.toArray

    let render values =
        arrayOf values |> Array.toList |> List.map itemOf |> XTrace.render

    let flatten messages : obj array =
        let typed =
            arrayOf messages
            |> Array.map (fun message ->
                { Role = text (field message "role")
                  Parts = arrayOf (field message "parts") |> Array.map semanticPartOf |> Array.toList })
            |> Array.toList

        XTrace.flatten typed
        |> List.map (fun entry ->
            box
                {| role = entry.Role
                   part = semanticPartView entry.Part |})
        |> List.toArray

    let snapshot (handle: JournalHandle) (sessionId: string) = traceOf handle sessionId

    let captureProjection (handle: JournalHandle) (sessionId: string) (projection: obj) : Task<obj> =
        task {
            let! result =
                XTraceCapture.captureProjectionWithReceipt
                    (Some handle.Journal)
                    (SessionId.create sessionId)
                    (projectionOf projection)

            return receiptView result
        }

    let captureOpening
        (handle: JournalHandle)
        (sessionId: string)
        (assignment: string)
        (requirements: string array)
        : Task<obj> =
        task {
            let! result =
                XTraceCapture.captureOpeningWithReceipt
                    (Some handle.Journal)
                    (SessionId.create sessionId)
                    assignment
                    (Array.toList requirements)

            return receiptView result
        }

    let captureTerminalText
        (handle: JournalHandle)
        (sessionId: string)
        (value: string)
        (providerRun: string)
        : Task<obj> =
        task {
            let! result =
                XTraceCapture.captureTerminalTextWithReceipt
                    (Some handle.Journal)
                    (SessionId.create sessionId)
                    value
                    (ProviderRunIdentity.create providerRun)

            return receiptView result
        }

    let captureLastWords
        (handle: JournalHandle)
        (sessionId: string)
        (textRef: string)
        (textDigest: string)
        (providerRun: string)
        : Task<obj> =
        task {
            let! result =
                XTraceCapture.captureLastWordsWithReceipt
                    (Some handle.Journal)
                    (SessionId.create sessionId)
                    (BlobRef.create textRef)
                    (BlobDigest.create textDigest)
                    (ProviderRunIdentity.create providerRun)

            return receiptView result
        }

    let captureMessageView (handle: JournalHandle) (sessionId: string) (messages: obj) : Task<obj> =
        task {
            let raw = arrayOf messages

            let captured =
                Wanxiangshu.OpenCode.ProviderWireCapture.decodeCapturedMessageView (Array.toList raw)

            let ids =
                raw
                |> Array.map (fun message -> text (field (field message "info") "id"))
                |> Array.toList

            let! result =
                XTraceCapture.captureMessageViewWithReceipt
                    (Some handle.Journal)
                    (SessionId.create sessionId)
                    (Some ids)
                    captured

            return receiptView result
        }

    let captureObservedMessages (handle: JournalHandle) (sessionId: string) (observations: obj) : Task<obj> =
        task {
            let typed =
                arrayOf observations
                |> Array.toList
                |> List.map (fun observation ->
                    let decoded =
                        Wanxiangshu.OpenCode.ProviderWireCapture.decodeCapturedMessageView
                            [ field observation "message" ]
                        |> List.head

                    let origin =
                        match optionalText (field observation "origin") with
                        | Some "ProviderRetryAttempt" ->
                            Some(
                                PromptAuthority.PromptOrigin.Continuation
                                    PromptAuthority.ContinuationKind.ProviderRetryAttempt
                            )
                        | _ -> None

                    { Message = decoded
                      HostMessageId = optionalText (field observation "hostMessageId")
                      Origin = origin })

            let! result =
                XTraceCapture.captureObservedMessagesWithReceipt
                    (Some handle.Journal)
                    (SessionId.create sessionId)
                    typed

            return
                match result with
                | Error error ->
                    box
                        {| ok = false
                           error = captureError error |}
                | Ok(captured: XTraceMessageCapture) ->
                    box
                        {| ok = true
                           receipt = receiptView (Ok captured.Receipt)
                           projection = captured.Current |> Option.map SemanticTraceProjection.Create |> Option.toObj |}
        }

    let currentProjection (handle: JournalHandle) (sessionId: string) : Task<obj> =
        task {
            let trace = traceOf handle sessionId

            match! XTraceMaterialization.currentProjection handle.Journal (stateOf trace) with
            | Ok projection -> return projectionView projection
            | Error error -> return raise (InvalidOperationException error)
        }

    let currentProjectionBetween (handle: JournalHandle) (sessionId: string) (range: obj) : Task<obj> =
        task {
            let trace = traceOf handle sessionId

            match! XTraceMaterialization.currentProjectionBetween handle.Journal (rangeOf range) (stateOf trace) with
            | Ok projection -> return projectionView projection
            | Error error -> return raise (InvalidOperationException error)
        }

    let renderRange (handle: JournalHandle) (sessionId: string) (range: obj) : Task<string> =
        task {
            let trace = traceOf handle sessionId

            match! XTraceMaterialization.renderRange handle.Journal (rangeOf range) (stateOf trace) with
            | Ok rendered -> return rendered
            | Error error -> return raise (InvalidOperationException error)
        }
