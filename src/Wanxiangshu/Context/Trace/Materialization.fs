namespace Wanxiangshu.Context.Trace

open System
open System.Threading.Tasks
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

    let private semanticPart (part: XTracePartRef) (body: string) =
        match part.Kind with
        | "text" -> Ok(ProviderProjection.SemanticText body)
        | "reasoning" -> Ok(ProviderProjection.SemanticReasoning body)
        | "tool_call" ->
            part.ToolName
            |> Option.map (fun name -> ProviderProjection.SemanticToolCall(name, body))
            |> Result.requireSome "XTrace tool_call is missing its tool name"
        | "tool_result" -> Ok(ProviderProjection.SemanticToolResult body)
        | "media_omitted" ->
            let mediaType = if String.IsNullOrWhiteSpace body then None else Some body
            Ok(ProviderProjection.SemanticMedia(mediaType, ""))
        | kind -> Error(sprintf "XTrace contains an unknown semantic part kind: %s" kind)

    let private readSemanticPart
        (journal: AgentJournal)
        (part: XTracePartRef)
        : Task<Result<ProviderProjection.SemanticPart, string>> =
        task {
            let! body = journal.Writer.BlobWriter.Read part.TextRef

            return
                body
                |> Result.mapError (fun error ->
                    sprintf "XTrace materialization could not read %s: %s" (BlobRef.value part.TextRef) error)
                |> Result.bind (semanticPart part)
        }

    let private readTurn
        (journal: AgentJournal)
        (_turn, parts: XTracePartRef list)
        : Task<Result<ProviderProjection.SemanticMessage, string>> =
        taskResult {
            let ordered = parts |> List.sortBy (fun part -> part.PartIndex)

            let role =
                ordered
                |> List.tryHead
                |> Option.map (fun part -> part.Role)
                |> Option.defaultValue "user"

            let! semanticParts = ordered |> TaskResultList.traverseM (readSemanticPart journal)

            return
                { ProviderProjection.SemanticMessage.Role = role
                  ProviderProjection.SemanticMessage.Parts = semanticParts }
        }

    let private projectionFromParts
        (journal: AgentJournal)
        (parts: XTracePartRef list)
        : Task<Result<ProviderProjection.ProviderSemanticProjection, string>> =
        taskResult {
            let turns = parts |> List.groupBy (fun part -> part.Turn) |> List.sortBy fst

            let! messages = turns |> TaskResultList.traverseM (readTurn journal)
            return { empty with Messages = messages }
        }

    /// Rebuild the current reanchor generation from durable XTrace only.
    /// Blob failure is a proof failure, not permission to silently omit history.
    let currentProjection
        (journal: AgentJournal)
        (xTrace: XTraceProjectionState)
        : Task<Result<ProviderProjection.ProviderSemanticProjection, string>> =
        XTraceProjection.currentGenerationParts (XTraceProjection.parts xTrace)
        |> projectionFromParts journal

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
        |> projectionFromParts journal

    let private readItem (journal: AgentJournal) (part: XTracePartRef) : Task<Result<XTraceItem, string>> =
        taskResult {
            let! semantic = readSemanticPart journal part

            return
                { Cursor = part.Cursor
                  Provenance = part.Provenance
                  Role = part.Role
                  Part = semantic }
        }

    /// Resolve one range to the owner model used by deterministic rendering.
    /// Missing/corrupt blobs fail closed rather than silently shortening proof.
    let materializeRange
        (journal: AgentJournal)
        (range: XTraceRange)
        (xTrace: XTraceProjectionState)
        : Task<Result<XTraceItem list, string>> =
        task {
            XTraceProjection.traceIntervalOfRange range xTrace |> ignore

            let selected =
                XTraceProjection.parts xTrace
                |> List.filter (fun part -> XTraceRange.contains part.Cursor range)

            let! resolved = selected |> TaskResultList.traverseM (readItem journal)
            return resolved |> Result.map (WanxiangliTrace.materializeItemsInRange range)
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
