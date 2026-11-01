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

    [<Import("PairGuidanceOccurrences", "wanxiangli/pair-hint")>]
    let private occurrencesClass: obj = jsNative

    let private occurrences = createNew occurrencesClass ()

    let private entryObject (entry: PairGuidanceOccurrence) =
        createObj
            [ "ordinal" ==> entry.Ordinal
              "callId" ==> entry.CallId
              "markerText" ==> entry.MarkerText
              "metadata" ==> box (entry.CallGap, entry.ResultGap) ]

    let private occurrenceFrom (entry: obj) : PairGuidanceOccurrence =
        let callGap, resultGap = unbox<TranscriptGap * TranscriptGap> entry?metadata
        { Ordinal = entry?ordinal
          CallId = entry?callId
          MarkerText = entry?markerText
          CallGap = callGap
          ResultGap = resultGap }

    let private resultObject = function
        | Ok value -> createObj [ "ok" ==> true; "value" ==> value ]
        | Error error -> createObj [ "ok" ==> false; "error" ==> error ]

    let private placementObject internalSession (transcript: PairGuidanceTranscript) =
        if internalSession || transcript.InternalAuthor then null
        else
            transcript.Carrier
            |> Result.map (Option.map (fun (callGap, resultGap) ->
                createObj
                    [ "callGapKey" ==> gapKey callGap
                      "resultGapKey" ==> gapKey resultGap
                      "metadata" ==> box (callGap, resultGap) ]) >> Option.defaultValue null)
            |> Result.mapError (fun () -> "guidance carrier without transcript address (HOST-013)")
            |> resultObject

    let occur
        (ledger: PairGuidanceLedger)
        (sessionId: string option)
        (internalSession: bool)
        (markerText: string)
        (transcript: PairGuidanceTranscript)
        (render: PairGuidanceOccurrence list -> Result<'Wire, string>)
        : Task<Result<'Wire * bool, string>> =
        task {
            let input =
                createObj
                    [ "history" ==> (ledger.History |> List.map entryObject |> List.toArray)
                      "visible" ==> (ledger.Visible |> List.map entryObject |> List.toArray)
                      "occupied" ==> (ledger.Visible |> List.map occupiedEntry |> List.toArray)
                      "syntheticCallIds" ==> List.toArray transcript.SyntheticCallIds
                      "placement" ==> placementObject internalSession transcript
                      "skipNew" ==> skipRequested ()
                      "sessionKey" ==> transcriptKey sessionId
                      "markerText" ==> markerText ]
            let renderNative = Func<obj array, obj>(fun entries -> entries |> Array.map occurrenceFrom |> Array.toList |> render |> resultObject)
            let appendNative = Func<obj, Task<obj>>(fun entry ->
                task {
                    let! result = ledger.Append(occurrenceFrom entry)
                    return result |> Result.map box |> Result.mapError (fun (PairGuidanceAppendError.JournalRejected reason) -> reason) |> resultObject
                })
            let! result: obj = occurrences?occur (input, renderNative, appendNative)
            if unbox<bool> result?ok then
                let value: obj = result?value
                return Ok(unbox<'Wire> value?wire, unbox<bool> value?appended)
            else
                let error: obj = result?error
                if unbox<string> error?kind = "Orphaned" then
                    let ids: string array = error?callIds
                    return Error(sprintf "synthetic messages without durable record (callId %s, HOST-013)" (String.Join(", ", ids |> Array.truncate 3)))
                else return Error(unbox<string> error?cause)
        }
