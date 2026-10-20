namespace Wanxiangshu.OpenCode

open Wanxiangshu.Persistence.Journal.JournalOutcome
open System
open System.Threading.Tasks
open Fable.Core.JsInterop
open Wanxiangshu.Composition.Durable
open Wanxiangshu.Composition.Durable.Fact
open Wanxiangshu.Context.Companion
open Wanxiangshu.Context.Trace
open Wanxiangshu.Host
open Wanxiangshu.Foundation.Identity
open Wanxiangshu.OpenCode.Host.PairProgramming
open Wanxiangshu.Persistence.Journal
open Wanxiangshu.Foundation

/// Pair-programming provider projection owner surface. The transform owns its
/// journal and placement invariants; callers observe only JSON messages/results.
module PairProgrammingThoughtSurface =

    type private JournalHandleBox(handle: JournalHandle) =
        member _.Value = handle

    let private journalHandleOf (value: obj) = (unbox<JournalHandleBox> value).Value

    let private agentJournalOf (value: obj) = (journalHandleOf value).Journal

    let private isNullish (value: obj) =
        isNull value || emitJsExpr value "$0 === undefined"

    let private textOf (value: obj) =
        if isNull value then "" else string value

    let private int64Of (value: obj) =
        if isNull value then 0L else int64 (textOf value)

    let private transcriptGapOf (value: obj) : TranscriptGap =
        if isNull value then
            TranscriptGap.Start
        elif emitJsExpr value "typeof $0 === 'string'" then
            let raw = textOf value

            if raw = "start" || raw = "Start" then
                TranscriptGap.Start
            elif raw.StartsWith("before:", StringComparison.OrdinalIgnoreCase) then
                TranscriptGap.Before(TranscriptMessageAddress.create (raw.Substring 7))
            elif raw.StartsWith("after:", StringComparison.OrdinalIgnoreCase) then
                TranscriptGap.After(TranscriptMessageAddress.create (raw.Substring 6))
            else
                TranscriptGap.After(TranscriptMessageAddress.create raw)
        else
            match textOf value?kind with
            | "start"
            | "Start" -> TranscriptGap.Start
            | "before"
            | "Before" -> TranscriptGap.Before(TranscriptMessageAddress.create (textOf value?id))
            | "after"
            | "After" -> TranscriptGap.After(TranscriptMessageAddress.create (textOf value?id))
            | _ -> TranscriptGap.After(TranscriptMessageAddress.create (textOf value?id))

    let private projectionFlagsOf (projection: AgentProjectionSet) (session: SessionId) : obj =
        match AgentProjection.tryFind session projection with
        | None -> box {| guidelines = false; xTrace = false |}
        | Some value ->
            box
                {| guidelines = value.Guidelines.IsSome
                   xTrace = XTraceProjection.hasSemanticParts value.XTrace |}

    let private anchoredFactOf (payload: obj) : AgentFact * SessionId =
        let sessionId = SessionId.create (textOf payload?session)

        let fact =
            HostFact.PairProgrammingGuidelineAnchored
                {| SessionId = sessionId
                   Ordinal = int64Of payload?ordinal
                   CallId = ToolCallId.create (textOf payload?callId)
                   MarkerText = textOf payload?markerText
                   CallGap = transcriptGapOf payload?callGapAfter
                   ResultGap = transcriptGapOf payload?resultGapAfter
                   ConcernPlacement = None |}

        fact, sessionId

    let private flagsForJournal (journal: obj) (session: string) : obj =
        let sessionId = SessionId.create session
        let projection = AgentJournal.snapshot (agentJournalOf journal)
        projectionFlagsOf projection.AgentProjections sessionId

    /// Boot the production EventStore journal behind one opaque capability.
    let createJournal (directory: string) : Task<obj> =
        task {
            let! result =
                JournalSurface.boot directory "pair-programming-surface" 0 (DateTimeOffset.UtcNow.ToString("O"))

            if isNullish result?ok || not (unbox<bool> result?ok) then
                return result
            else
                let handle = unbox<JournalHandle> result?journal

                return
                    box
                        {| ok = true
                           journal = (JournalHandleBox handle :> obj) |}
        }

    let disposeJournal (journal: obj) : unit =
        JournalSurface.dispose (journalHandleOf journal)

    /// Append one anchored guideline fact and return only the owner projection flags.
    let appendAnchoredPair (journal: obj) (payload: obj) : Task<obj> =
        task {
            if isNullish journal then
                return
                    box
                        {| ok = false
                           error = "journal required" |}
            else
                let fact, sessionId = anchoredFactOf payload
                let! result = AgentJournal.appendAgent (StreamId.Session sessionId) None fact (agentJournalOf journal)

                return
                    match result with
                    | Ok projection ->
                        projectionFlagsOf projection.AgentProjections sessionId
                        |> fun flags -> box {| ok = true; flags = flags |}
                    | Error failure ->
                        box
                            {| ok = false
                               error = JournalAppendFailure.describe failure |}
        }

    let pairCount (journal: obj) (session: string) : int =
        let projection = AgentJournal.snapshot (agentJournalOf journal)

        AgentProjection.tryFind (SessionId.create session) projection.AgentProjections
        |> Option.bind (fun value -> value.Guidelines)
        |> Option.map (GuidelineProjection.pairs >> List.length)
        |> Option.defaultValue 0

    let appendContextReanchored
        (journal: obj)
        (session: string)
        (previousEpoch: int64)
        (nextEpoch: int64)
        (observedRun: string)
        : Task<obj> =
        task {
            let sessionId = SessionId.create session

            let fact =
                ContextFact.ContextReanchored
                    {| SessionId = sessionId
                       PreviousEpochId = PrefixEpochId.create previousEpoch
                       NextEpochId = PrefixEpochId.create nextEpoch
                       ObservedCompactionRun = ProviderRunIdentity.create observedRun |}

            let! result = AgentJournal.appendAgent (StreamId.Session sessionId) None fact (agentJournalOf journal)

            return
                match result with
                | Ok _ -> box {| ok = true; error = null |}
                | Error failure ->
                    box
                        {| ok = false
                           error = JournalAppendFailure.describe failure |}
        }

    let projectionFlags (journal: obj) (session: string) : obj = flagsForJournal journal session

    /// Fold one anchored pair without opening a journal; used by the pure law test.
    let foldAnchoredPair (payload: obj) : obj =
        let fact, sessionId = anchoredFactOf payload

        match Fold.foldAgentFact AgentProjection.empty fact with
        | Ok projection ->
            projectionFlagsOf projection sessionId
            |> fun flags -> box {| ok = true; flags = flags |}
        | Error rejection ->
            box
                {| ok = false
                   error = rejection.Reason |}

    let text = PairProgrammingThoughtTransform.text
    let source = PairProgrammingThoughtTransform.source
    let markerSource = source
    let markerToolName = PairProgrammingThoughtTransform.toolName
    let canonicalText = text
    let deniedText = PairProgrammingThoughtTransform.reprimandText None

    let private gapName gap =
        match gap with
        | TranscriptGap.Start -> "start"
        | TranscriptGap.Before address -> "before:" + TranscriptMessageAddress.value address
        | TranscriptGap.After address -> "after:" + TranscriptMessageAddress.value address

    /// Executes the production pair constructor with an observable pure callback.
    let gapConstructorTrace (messageId: string) : obj =
        // DSL-MUTABLE: algorithm-scratch — exact constructor call trace within one proof invocation.
        let mutable inputs: string list = []

        let ctor address =
            inputs <- TranscriptMessageAddress.value address :: inputs
            TranscriptGap.After address

        let message =
            box
                {| info = {| id = messageId; role = "assistant" |}
                   parts = [||] |}

        match PairProgrammingThoughtTransform.gapsAroundAddress ctor message "missing address" with
        | Ok(left, right) ->
            box
                {| ok = true
                   inputs = inputs |> List.rev |> List.toArray
                   left = gapName left
                   right = gapName right |}
        | Error error ->
            box
                {| ok = false
                   inputs = inputs |> List.rev |> List.toArray
                   left = error
                   right = error |}

    /// Observes fail-fast behavior without weakening the production exception edge.
    let gapConstructorFailureTrace (messageId: string) : obj =
        // DSL-MUTABLE: algorithm-scratch — invocations before the thrown callback failure.
        let mutable calls = 0

        let message =
            box
                {| info = {| id = messageId; role = "assistant" |}
                   parts = [||] |}

        try
            PairProgrammingThoughtTransform.gapsAroundAddress
                (fun _ ->
                    calls <- calls + 1
                    invalidOp "gap-constructor-failed")
                message
                "missing address"
            |> ignore

            box {| calls = calls; error = "" |}
        with error ->
            box
                {| calls = calls
                   error = error.Message |}

    let stableCallId (sessionId: obj) (ordinal: int64) : string =
        PairProgrammingThoughtTransform.stableCallId (if isNull sessionId then None else Some(string sessionId)) ordinal

    let isPairProgrammingThought (raw: obj) : bool =
        PairProgrammingThoughtTransform.isPairProgrammingThought raw

    let providerIdOfMessage (raw: obj) : obj =
        PairProgrammingThoughtTransform.providerIdOfMessage raw |> Option.toObj |> box

    let providerIdFromMessages (raw: obj array) : obj =
        let messages = if isNull raw then [] else Array.toList raw

        PairProgrammingThoughtTransform.providerIdFromMessages messages
        |> Option.toObj
        |> box

    let skipAutoInjectedRequested (providerId: obj) : bool =
        PairProgrammingThoughtTransform.skipAutoInjectedRequested (
            if isNull providerId then None else Some(string providerId)
        )

    let tryInject (sessionId: obj) (markerText: string) (rawMessages: obj array) : Task<obj> =
        task {
            let session = if isNull sessionId then None else Some(string sessionId)
            let! result = PairProgrammingThoughtTransform.tryInject None session markerText (Array.toList rawMessages)

            return
                match result with
                | Ok messages ->
                    box
                        {| ok = true
                           value = messages |> List.toArray |}
                | Error error -> box {| ok = false; error = error |}
        }

    /// Durable owner path for anchored replay tests. The journal remains opaque to
    /// callers; only the transform may read/append its pair-placement facts.
    let tryInjectWithJournal (journal: obj) (sessionId: obj) (markerText: string) (rawMessages: obj array) : Task<obj> =
        task {
            let durableJournal =
                if isNullish journal then
                    None
                else
                    Some(agentJournalOf journal)

            let session = if isNull sessionId then None else Some(string sessionId)
            let messages = if isNull rawMessages then [] else Array.toList rawMessages
            let! result = PairProgrammingThoughtTransform.tryInject durableJournal session markerText messages

            return
                match result with
                | Ok values ->
                    box
                        {| ok = true
                           value = values |> List.toArray |}
                | Error error -> box {| ok = false; error = error |}
        }
