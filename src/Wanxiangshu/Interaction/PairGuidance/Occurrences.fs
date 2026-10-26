namespace Wanxiangshu.Interaction.PairGuidance

open System
open System.Collections.Generic
open System.Threading.Tasks
open Fable.Core
open Fable.Core.JsInterop
open FsToolkit.ErrorHandling
open Wanxiangshu.Foundation
open Wanxiangshu.Foundation.Identity
open Wanxiangshu.Host
open Wanxiangshu.Host.Domain
open Wanxiangshu.Interaction.Authority
open Wanxiangshu.Persistence.Journal
open Wanxiangshu.Persistence.Journal.JournalOutcome
open Wanxiangshu.Composition.Durable
open Wanxiangshu.Execution.Session

/// HOST-013: one pair-guidance occurrence, what it said and where it sits
/// on the transcript. Product state: the product owns the occurrence order.
type PairGuidanceOccurrence =
    { Ordinal: int64
      CallId: string
      MarkerText: string
      CallGap: TranscriptGap
      ResultGap: TranscriptGap }

/// Why an occurrence could not be recorded. A product decision about the
/// product's own durable journal — never a host refusal.
[<RequireQualifiedAccess>]
type PairGuidanceAppendError =
    /// The durable journal refused the anchored-guideline fact.
    | JournalRejected of reason: string

/// HOST-013: the occurrence ledger of one transcript.
type PairGuidanceLedger =
    { History: PairGuidanceOccurrence list
      Visible: PairGuidanceOccurrence list
      Append: PairGuidanceOccurrence -> Task<Result<unit, PairGuidanceAppendError>> }

[<RequireQualifiedAccess>]
module PairGuidanceOccurrences =

    [<Import("pairGuidanceMemoryStore", "wanxiangli/pair-hint")>]
    let private pairGuidanceMemoryStore: obj = jsNative

    [<Import("pairGuidanceSource", "wanxiangli/pair-hint")>]
    let private pairGuidanceSourceVal: string = jsNative

    let source = pairGuidanceSourceVal

    let private transcriptKey (sessionId: string option) : string = defaultArg sessionId ""

    let stableCallId (sessionId: string option) (ordinal: int64) : string =
        pairGuidanceMemoryStore?stableCallId (transcriptKey sessionId, ordinal)

    let skipRequested () : bool =
        match Environment.GetEnvironmentVariable "WANXIANGSHU_SKIP_AUTO_INJECTED" with
        | "1" -> true
        | _ -> false

    let private occurrenceOf (pair: PairProgrammingGuideline) : PairGuidanceOccurrence =
        { Ordinal = pair.Ordinal
          CallId = ToolCallId.value pair.CallId
          MarkerText = pair.MarkerText
          CallGap = pair.CallGap
          ResultGap = pair.ResultGap }

    let private durablePairs
        (select: GuidelineProjectionState -> PairProgrammingGuideline list)
        (journal: AgentJournal)
        (sessionId: SessionId)
        : PairGuidanceOccurrence list =
        match AgentProjection.tryFind sessionId (AgentJournal.snapshot journal).AgentProjections with
        | None -> []
        | Some session ->
            session.Guidelines
            |> Option.map select
            |> Option.defaultValue []
            |> List.map occurrenceOf

    let private appendDurable
        (journal: AgentJournal)
        (sessionId: SessionId)
        (occurrence: PairGuidanceOccurrence)
        : Task<Result<unit, PairGuidanceAppendError>> =
        task {
            let fact =
                HostFact.PairProgrammingGuidelineAnchored
                    {| SessionId = sessionId
                       Ordinal = occurrence.Ordinal
                       CallId = ToolCallId.create occurrence.CallId
                       MarkerText = occurrence.MarkerText
                       CallGap = occurrence.CallGap
                       ResultGap = occurrence.ResultGap |}

            match! AgentJournal.appendAgent (StreamId.Session sessionId) None fact journal with
            | Ok _ -> return Ok()
            | Error failure ->
                return Error(PairGuidanceAppendError.JournalRejected(JournalAppendFailure.describe failure))
        }

    let private entryOrdinal (entry: obj) : int64 =
        if isNull entry?ordinal then int64 (string entry?Ordinal) else int64 (string entry?ordinal)

    let private entryCallId (entry: obj) : string =
        if isNull entry?callId then string entry?CallId else string entry?callId

    let private entryMarkerText (entry: obj) : string =
        if isNull entry?markerText then string entry?MarkerText else string entry?markerText

    let private unboxEntry (entry: obj) : PairGuidanceOccurrence option =
        if isNull entry then None
        elif not (isNull entry?raw) then Some(unbox<PairGuidanceOccurrence> entry?raw)
        else
            Some
                { Ordinal = entryOrdinal entry
                  CallId = entryCallId entry
                  MarkerText = entryMarkerText entry
                  CallGap = TranscriptGap.Start
                  ResultGap = TranscriptGap.Start }

    let private readMemory (key: string) : PairGuidanceOccurrence list =
        let entries: obj array = pairGuidanceMemoryStore?read (key)
        entries
        |> Option.ofObj
        |> Option.defaultValue [||]
        |> Array.choose unboxEntry
        |> Array.toList

    let private appendMemory
        (key: string)
        (occurrence: PairGuidanceOccurrence)
        : Task<Result<unit, PairGuidanceAppendError>> =
        pairGuidanceMemoryStore?append (key, occurrence)
        Task.FromResult(Ok())

    let ledger (journal: AgentJournal option) (sessionId: string option) : PairGuidanceLedger =
        match journal, sessionId with
        | Some durable, Some sid when not (String.IsNullOrWhiteSpace sid) ->
            let session = SessionId.create sid

            { History = durablePairs GuidelineProjection.pairs durable session
              Visible = durablePairs GuidelineProjection.visiblePairs durable session
              Append = appendDurable durable session }
        | _ ->
            let key = transcriptKey sessionId
            let memory = readMemory key

            { History = memory
              Visible = memory
              Append = appendMemory key }

    let private sessionRole (durable: AgentJournal) (sessionId: SessionId) : Role option =
        let projections = (AgentJournal.snapshot durable).AgentProjections

        PromptAuthorityProjectionQueries.activeProfile sessionId projections
        |> Option.orElseWith (fun () -> PromptAuthorityProjectionQueries.lastAuthorityProfile sessionId projections)
        |> Option.map (fun profile -> profile.CanonicalRole)
        |> Option.orElseWith (fun () ->
            projections.HandleByChildSession
            |> Map.tryFind sessionId
            |> Option.map (fun handle -> handle.CanonicalRole))

    let isInternalSession (journal: AgentJournal option) (sessionId: string option) : bool =
        match journal, sessionId with
        | Some durable, Some sid when not (String.IsNullOrWhiteSpace sid) ->
            let session = SessionId.create sid
            let snapshot = AgentJournal.snapshot durable

            SessionAssociationProjection.isCompanion session snapshot.AgentProjections.Associations
            || (sessionRole durable session |> Option.exists Roles.isInternal)
        | _ -> false

    let private gapKey (gap: TranscriptGap) : string =
        match gap with
        | TranscriptGap.Start -> "Start"
        | TranscriptGap.Before addr -> "Before:" + TranscriptMessageAddress.value addr
        | TranscriptGap.After addr -> "After:" + TranscriptMessageAddress.value addr

    let private occupiedEntry (pair: PairGuidanceOccurrence) : obj =
        box {| callGapKey = gapKey pair.CallGap; resultGapKey = gapKey pair.ResultGap |}

    let private nextOrdinal (history: PairGuidanceOccurrence list) : int64 =
        let mapped = history |> List.toArray |> Array.map box
        pairGuidanceMemoryStore?nextOrdinal (mapped)

    let private occupied (visible: PairGuidanceOccurrence list) (callGap: TranscriptGap) (resultGap: TranscriptGap) : bool =
        let mapped = visible |> List.map occupiedEntry |> List.toArray
        pairGuidanceMemoryStore?isOccupied (mapped, gapKey callGap, gapKey resultGap)

    let private formatOrphanError (orphaned: string list) : string =
        sprintf
            "synthetic messages without durable record (callId %s, HOST-013)"
            (String.Join(", ", orphaned |> List.truncate 3))

    let private orphanCheck (ledger: PairGuidanceLedger) (transcript: PairGuidanceTranscript) =
        let hist = ledger.History |> List.toArray |> Array.map box
        let syn = transcript.SyntheticCallIds |> List.toArray
        let orphaned: string list =
            pairGuidanceMemoryStore?findOrphanedCallIds (hist, syn)
            |> unbox<string array>
            |> Array.toList
        Result.requireTrue (formatOrphanError orphaned) (List.isEmpty orphaned)

    /// The product's own prose about a failed append.
    let private describeAppendFailure (failure: PairGuidanceAppendError) =
        match failure with
        | PairGuidanceAppendError.JournalRejected reason -> reason

    let private replayOccurrence
        (render: PairGuidanceOccurrence list -> Result<'Wire, string>)
        (visible: PairGuidanceOccurrence list)
        : Task<Result<'Wire * bool, string>> =
        task {
            match render visible with
            | Ok replayed -> return Ok(replayed, false)
            | Error e -> return Error e
        }

    let private appendOccurrenceResult
        (rendered: 'Wire)
        (ledger: PairGuidanceLedger)
        (candidate: PairGuidanceOccurrence)
        : Task<Result<'Wire * bool, string>> =
        task {
            let! appendResult = ledger.Append candidate

            match appendResult with
            | Ok() -> return Ok(rendered, true)
            | Error failure -> return Error(describeAppendFailure failure)
        }

    let private commitNewOccurrence
        (render: PairGuidanceOccurrence list -> Result<'Wire, string>)
        (ledger: PairGuidanceLedger)
        (candidate: PairGuidanceOccurrence)
        : Task<Result<'Wire * bool, string>> =
        match render (ledger.Visible @ [ candidate ]) with
        | Error e -> Task.FromResult(Error e)
        | Ok rendered -> appendOccurrenceResult rendered ledger candidate

    let private commitAt
        (ledger: PairGuidanceLedger)
        (sessionId: string option)
        (markerText: string)
        (render: PairGuidanceOccurrence list -> Result<'Wire, string>)
        (callGap: TranscriptGap)
        (resultGap: TranscriptGap)
        : Task<Result<'Wire * bool, string>> =
        if occupied ledger.Visible callGap resultGap || skipRequested () then
            replayOccurrence render ledger.Visible
        else
            let ordinal = nextOrdinal ledger.History

            let candidate =
                { Ordinal = ordinal
                  CallId = stableCallId sessionId ordinal
                  MarkerText = markerText
                  CallGap = callGap
                  ResultGap = resultGap }

            commitNewOccurrence render ledger candidate

    /// Internal transcripts never carry a new occurrence, whatever the host decoded.
    let private placementOf
        (internalTranscript: bool)
        (transcript: PairGuidanceTranscript)
        : Result<(TranscriptGap * TranscriptGap) option, string> =
        if internalTranscript then
            Ok None
        else
            transcript.Carrier
            |> Result.mapError (fun () -> "guidance carrier without transcript address (HOST-013)")

    let occur
        (ledger: PairGuidanceLedger)
        (sessionId: string option)
        (internalSession: bool)
        (markerText: string)
        (transcript: PairGuidanceTranscript)
        (render: PairGuidanceOccurrence list -> Result<'Wire, string>)
        : Task<Result<'Wire * bool, string>> =
        taskResult {
            do! orphanCheck ledger transcript
            let! placement = placementOf (internalSession || transcript.InternalAuthor) transcript

            match placement with
            | None ->
                let! replayed = render ledger.Visible
                return (replayed, false)
            | Some(callGap, resultGap) -> return! commitAt ledger sessionId markerText render callGap resultGap
        }
