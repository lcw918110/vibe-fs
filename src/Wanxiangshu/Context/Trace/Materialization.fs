namespace Wanxiangshu.Context.Trace

open System
open System.Threading.Tasks
open Fable.Core.JsInterop
open FsToolkit.ErrorHandling
open Wanxiangshu.Foundation
open Wanxiangshu.Foundation.Identity
open Wanxiangshu.Participant.Provider.Projection
open Wanxiangshu.Persistence.Journal
open Wanxiangshu.Host

/// Durable XTrace -> canonical X semantic projection.
///
/// Request-local provider presentation is intentionally absent from this owner.
[<RequireQualifiedAccess>]
module XTraceMaterialization =

    let empty: ProviderProjection.ProviderSemanticProjection =
        { ProviderId = None
          ModelId = None
          Variant = None
          Tools = []
          System = []
          Messages = [] }

    let tryHostToolResult
        (journal: AgentJournal)
        (messageId: TranscriptMessageAddress)
        (partId: HostToolPartId option)
        (callId: ToolCallId)
        (xTrace: XTraceProjectionState)
        : Task<Result<string option, string>> =
        let candidates =
            XTraceProjection.partsForHostMessageIds (Set.singleton (TranscriptMessageAddress.value messageId)) xTrace
            |> List.filter (fun part ->
                part.Kind = "tool_result"
                && part.HostToolPartId = partId
                && part.ToolCallId = Some callId)

        taskResult {
            match candidates with
            | [] -> return None
            | [ part ] ->
                let! body = journal.Writer.BlobWriter.Read part.TextRef

                do!
                    Result.requireTrue
                        "XTrace original Host tool result digest does not match its captured fact"
                        (HostDigest.sha256Hex body = BlobDigest.value part.TextDigest)

                return Some body
            | _ -> return! Error "XTrace original Host tool result identity is ambiguous"
        }

    let private readBody (journal: AgentJournal) =
        Func<string, Task<obj>>(fun reference ->
            task {
                let! result = journal.Writer.BlobWriter.Read(BlobRef.create reference)
                return
                    match result with
                    | Ok body -> createObj [ "ok" ==> true; "value" ==> body ]
                    | Error error -> createObj [ "ok" ==> false; "error" ==> error ]
            })

    let private readResult (pending: Task<obj>) : Task<Result<obj array, string>> =
        task {
            let! result = pending
            return
                if unbox<bool> result?ok then Ok(unbox<obj array> result?value)
                else Error(unbox<string> result?error)
        }

    let private readTurn
        (journal: AgentJournal)
        (history: WanxiangliTrace.History)
        (_turn, parts: XTracePartRef list)
        : Task<Result<ProviderProjection.SemanticMessage, string>> =
        taskResult {
            let ordered = parts |> List.sortBy (fun part -> part.PartIndex)

            let role =
                ordered
                |> List.tryHead
                |> Option.map (fun part -> part.Role)
                |> Option.defaultValue "user"

            let! items =
                history.read(ordered |> List.map XTraceProjection.nativePart |> List.toArray, readBody journal)
                |> readResult

            let semanticParts =
                items |> Array.map (fun item -> WanxiangliTrace.semanticFromNative item?part) |> Array.toList

            return
                { ProviderProjection.SemanticMessage.Role = role
                  ProviderProjection.SemanticMessage.Parts = semanticParts }
        }

    let private projectionFromParts
        (journal: AgentJournal)
        (xTrace: XTraceProjectionState)
        (parts: XTracePartRef list)
        : Task<Result<ProviderProjection.ProviderSemanticProjection, string>> =
        taskResult {
            let turns = parts |> List.groupBy (fun part -> part.Turn) |> List.sortBy fst

            let! messages = turns |> TaskResultList.traverseM (readTurn journal (XTraceProjection.history xTrace))
            return { empty with Messages = messages }
        }

    /// 从已保存的材料重建当前宿主 generation。
    let currentProjection
        (journal: AgentJournal)
        (xTrace: XTraceProjectionState)
        : Task<Result<ProviderProjection.ProviderSemanticProjection, string>> =
        XTraceProjection.currentGenerationParts (XTraceProjection.parts xTrace)
        |> projectionFromParts journal xTrace

    /// Canonical current-generation semantic projection restricted to a
    /// trace-owned half-open interval.
    let currentProjectionBetween
        (journal: AgentJournal)
        (range: XTraceRange)
        (xTrace: XTraceProjectionState)
        : Task<Result<ProviderProjection.ProviderSemanticProjection, string>> =
        XTraceProjection.traceIntervalOfRange range xTrace |> ignore

        XTraceProjection.currentGenerationParts (XTraceProjection.parts xTrace)
        |> List.filter (fun part -> XTraceRange.contains part.Cursor range)
        |> projectionFromParts journal xTrace

    /// 读取固定区间，并保留既有的字节分块还原。
    let materializeRange
        (journal: AgentJournal)
        (range: XTraceRange)
        (xTrace: XTraceProjectionState)
        : Task<Result<XTraceItem list, string>> =
        task {
            let! resolved =
                (XTraceProjection.history xTrace).materialize(WanxiangliTrace.rangeObject range, readBody journal)
                |> readResult

            return
                resolved
                |> Result.map (Array.map (WanxiangliTrace.itemFromNative XTraceProjection.provenanceFromMetadata) >> Array.toList)
        }

    let renderRange
        (journal: AgentJournal)
        (range: XTraceRange)
        (xTrace: XTraceProjectionState)
        : Task<Result<string, string>> =
        task {
            let! resolved = materializeRange journal range xTrace
            return resolved |> Result.map XTrace.render
        }
