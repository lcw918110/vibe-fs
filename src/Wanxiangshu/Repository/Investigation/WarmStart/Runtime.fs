namespace Wanxiangshu.Repository.Investigation.WarmStart

open Wanxiangshu.Repository.Investigation.Semble
open System
open System.IO
open System.Threading.Tasks
open Fable.Core
open Wanxiangshu.Foundation
open Wanxiangshu.Participant.Provider
open Wanxiangshu.Foundation.Identity

/// AGENT-032: Infrastructure adapter from explicit keywords to neutral warm-start
/// material. Semble remains internal and fail-open; provider rendering stays Domain.
[<RequireQualifiedAccess>]
module RepositoryWarmStart =

    [<Import("mapBounded", "wanxiangli/bounded-map")>]
    let private mapBoundedLibrary: obj = jsNative

    [<Emit("$0($1, $2, $3)")>]
    let private mapBounded<'T, 'U>
        (library: obj)
        (limit: int)
        (action: Func<'T, obj, Task<'U>>)
        (items: 'T array)
        : Task<'U array> = jsNative

    type Search = string -> string -> int -> Task<SembleMcp.Hit list>

    let private toHint keywordOrdinal localRank (hit: SembleMcp.Hit) : RepositoryWarmStartHint =
        { KeywordOrdinal = keywordOrdinal
          LocalRank = localRank
          FilePath = hit.FilePath
          StartLine = hit.StartLine
          EndLine = hit.EndLine
          Content = hit.Content
          Score = hit.Score
          TotalLines = hit.TotalLines }

    let private trySearch (search: Search) repo query =
        task {
            try
                return! search query repo RepositoryWarmStartPrompt.TopKPerKeyword
            with _ ->
                // One query is an optimization shard; its failure
                // cannot fail or serialize the invocation.
                return []
        }

    let private runSearchShard (search: Search) repo (ordinal, query) =
        task {
            let! hits = trySearch search repo query

            let neutral = RepositoryWarmStartPrompt.rankHits ordinal hits toHint

            return
                { RepositoryWarmStartSearch.Ordinal = ordinal
                  Query = query
                  Hints = neutral }
        }

    let private collectAtWorkspace (search: Search) (workspaceDirectory: string option) keywords =
        task {
            match workspaceDirectory with
            | None -> return Ok None
            | Some repo when String.IsNullOrWhiteSpace repo || not (Directory.Exists repo) -> return Ok None
            | Some repo ->
                let indexed = keywords |> List.mapi (fun index query -> index + 1, query)

                let! searches =
                    mapBounded
                        mapBoundedLibrary
                        RepositoryWarmStartPrompt.MaxKeywords
                        (Func<_, _, _>(fun item _ -> runSearchShard search repo item))
                        (List.toArray indexed)

                // Keyword-ordinal merge order is the warm-start law's (wanxiangli/warm-start select).
                return Ok(Some(List.ofArray searches))
        }

    let private collectWithSearch
        (search: Search)
        (role: Role)
        (workspaceDirectory: string option)
        (keywordsRaw: string)
        : Task<Result<RepositoryWarmStartSearch list option, string>> =
        task {
            let keywords = RepositoryWarmStartPrompt.normalizeKeywords keywordsRaw

            if List.isEmpty keywords then
                // None = true zero-work fast path. Callers preserve their base prompt byte-for-byte.
                return Ok None
            elif not (RepositoryWarmStartPrompt.isDirectConsumer role) then
                return Error "repository warm-start keywords are only available to Engineer or DevOps targets"
            else
                return! collectAtWorkspace search workspaceDirectory keywords
        }

    let prepareDocumentWithSearch
        (search: Search)
        (sessionId: SessionId)
        (role: Role)
        (workspaceDirectory: string option)
        (keywordsRaw: string)
        (charge: string)
        : Task<Result<LlmFacing.Document, string>> =
        task {
            match! collectWithSearch search role workspaceDirectory keywordsRaw with
            | Error error -> return Error error
            | Ok None -> return Ok(LlmFacing.instruction charge)
            | Ok(Some searches) ->
                let lang = ProviderProse.languageOf sessionId

                let instructions =
                    ProviderProse.instructionLines
                        lang
                        RepositoryWarmStartPrompt.ChargeEnvelope
                        (Map [ "charge", charge ])

                return Ok(RepositoryWarmStartPrompt.buildDocument instructions charge searches)
        }

    let prepareWithSearch
        (search: Search)
        (sessionId: SessionId)
        (role: Role)
        (workspaceDirectory: string option)
        (keywordsRaw: string)
        (charge: string)
        : Task<Result<string, string>> =
        prepareDocumentWithSearch search sessionId role workspaceDirectory keywordsRaw charge
        |> TaskValue.map (Result.map LlmFacing.render)

    /// Fork path: preserve ForkChildPayload's assignment + commissioner record
    /// and append repository_search/repository_hint tables as low-trust data.
    let appendToBaseDocumentWithSearch
        (search: Search)
        (sessionId: SessionId)
        (role: Role)
        (workspaceDirectory: string option)
        (keywordsRaw: string)
        (baseDocument: LlmFacing.Document)
        : Task<Result<LlmFacing.Document, string>> =
        task {
            match! collectWithSearch search role workspaceDirectory keywordsRaw with
            | Error error -> return Error error
            | Ok None -> return Ok baseDocument
            | Ok(Some searches) ->
                let appendix =
                    ProviderProse.instructionLines
                        (ProviderProse.languageOf sessionId)
                        RepositoryWarmStartPrompt.Appendix
                        Map.empty

                return Ok(RepositoryWarmStartPrompt.appendToDocument appendix baseDocument searches)
        }

    let appendToBaseWithSearch
        (search: Search)
        (sessionId: SessionId)
        (role: Role)
        (workspaceDirectory: string option)
        (keywordsRaw: string)
        (basePrompt: string)
        : Task<Result<string, string>> =
        appendToBaseDocumentWithSearch
            search
            sessionId
            role
            workspaceDirectory
            keywordsRaw
            (LlmFacing.instruction basePrompt)
        |> TaskValue.map (Result.map LlmFacing.render)

    let prepare
        (sessionId: SessionId)
        (role: Role)
        (workspaceDirectory: string option)
        (keywordsRaw: string)
        (charge: string)
        : Task<Result<string, string>> =
        prepareWithSearch SembleMcpClient.searchFromEnvironment sessionId role workspaceDirectory keywordsRaw charge

    let appendToBaseDocument
        (sessionId: SessionId)
        (role: Role)
        (workspaceDirectory: string option)
        (keywordsRaw: string)
        (baseDocument: LlmFacing.Document)
        : Task<Result<LlmFacing.Document, string>> =
        appendToBaseDocumentWithSearch
            SembleMcpClient.searchFromEnvironment
            sessionId
            role
            workspaceDirectory
            keywordsRaw
            baseDocument
