namespace Wanxiangshu.Context.Prefix

open Fable.Core
open Fable.Core.JsInterop
open Wanxiangshu.Foundation.Identity
open Wanxiangshu.Foundation

type ArchivedScope =
    { SourceEpochId: PrefixEpochId
      TargetEpochId: PrefixEpochId
      ResetCallId: string
      ArchiveRef: BlobRef
      ArchiveDigest: BlobDigest }

/// Which prefix generation is in force, as the durable projection holds it.
///
/// The generation rules — which evidence may advance it, candidate versus commit,
/// which refusals are absorbed — are owned by `wanxiangli/prefix` (CUT-15). This
/// record is only the projection's typed copy of that owner's plain state.
type ActivePrefixEpoch =
    {
        EpochId: PrefixEpochId
        /// HOST-006: compaction pseudo-runs that already advanced the epoch.
        ReanchoredRuns: Set<ProviderRunIdentity>
        /// Plan tenure incumbencies that already advanced the epoch.
        ReanchoredTenures: Set<string>
        ReanchoredScopes: Set<string>
        LastScopeArchive: ArchivedScope option
        ScopeQuestions: Map<string, string>
    }

/// Why a prefix-epoch line was refused, in this repository's vocabulary.
[<RequireQualifiedAccess>]
type PrefixFoldRejection =
    | StalePrefixEpoch of expected: PrefixEpochId * actual: PrefixEpochId
    | NonSequentialPrefixEpoch
    | CompactionAlreadyReanchored of run: ProviderRunIdentity
    | TenureAlreadyReanchored of incumbencyId: string
    | ScopeAlreadyReanchored of callId: string

/// One proposed epoch-advancing line. Proposing changes nothing; only a fold of
/// the written line commits it.
type PrefixReanchorCandidate =
    { PreviousEpochId: PrefixEpochId
      NextEpochId: PrefixEpochId
      CompactionRun: ProviderRunIdentity }

module PrefixEpochProjection =

    [<Import("Prefix", "wanxiangli/prefix")>]
    let private prefixOwnerCtor: obj = jsNative

    let private prefixOwner: obj = createNew prefixOwnerCtor ()

    let empty =
        { EpochId = PrefixEpochId.initial
          ReanchoredRuns = Set.empty
          ReanchoredTenures = Set.empty
          ReanchoredScopes = Set.empty
          LastScopeArchive = None
          ScopeQuestions = Map.empty }

    let private epochNumber (epoch: PrefixEpochId) : int64 = PrefixEpochId.value epoch

    let private epochOf (value: obj) : PrefixEpochId =
        PrefixEpochId.create (unbox<int64> (emitJsExpr value "BigInt($0)"))

    let private stateView (state: ActivePrefixEpoch) : obj =
        let baseObj =
            createObj
                [ "epoch" ==> epochNumber state.EpochId
                  "compactionRuns"
                  ==> (state.ReanchoredRuns |> Set.toArray |> Array.map ProviderRunIdentity.value)
                  "tenures" ==> (state.ReanchoredTenures |> Set.toArray) ]

        if not (Set.isEmpty state.ReanchoredScopes) then
            baseObj?scopes <- (state.ReanchoredScopes |> Set.toArray)

        baseObj

    let private stateOf (view: obj) : ActivePrefixEpoch =
        { EpochId = epochOf view?epoch
          ReanchoredRuns =
            unbox<string array> view?compactionRuns
            |> Array.map ProviderRunIdentity.create
            |> Set.ofArray
          ReanchoredTenures = unbox<string array> view?tenures |> Set.ofArray
          ReanchoredScopes =
            if emitJsExpr view "'scopes' in Object($0)" then
                unbox<string array> view?scopes |> Set.ofArray
            else
                Set.empty
          LastScopeArchive = None
          ScopeQuestions = Map.empty }

    let private compaction (run: ProviderRunIdentity) : obj =
        createObj [ "kind" ==> "compaction"; "run" ==> ProviderRunIdentity.value run ]

    let private tenure (incumbencyId: string) : obj =
        createObj [ "kind" ==> "tenure"; "incumbencyId" ==> incumbencyId ]

    let private scope (callId: string) : obj =
        createObj [ "kind" ==> "scope"; "callId" ==> callId ]

    let private rejectionOf (result: obj) : PrefixFoldRejection =
        let evidence = result?evidence

        match string result?error, (if isNull evidence then "" else string evidence?kind) with
        | "StaleEpoch", _ -> PrefixFoldRejection.StalePrefixEpoch(epochOf result?expected, epochOf result?actual)
        | "AlreadyReanchored", "tenure" -> PrefixFoldRejection.TenureAlreadyReanchored(string evidence?incumbencyId)
        | "AlreadyReanchored", "scope" -> PrefixFoldRejection.ScopeAlreadyReanchored(string evidence?callId)
        | "AlreadyReanchored", _ ->
            PrefixFoldRejection.CompactionAlreadyReanchored(ProviderRunIdentity.create (string evidence?run))
        | _ -> PrefixFoldRejection.NonSequentialPrefixEpoch

    let private commit
        (previousEpoch: PrefixEpochId)
        (nextEpoch: PrefixEpochId)
        (evidence: obj)
        (state: ActivePrefixEpoch)
        : Result<ActivePrefixEpoch, PrefixFoldRejection> =
        let candidate =
            createObj
                [ "previousEpoch" ==> epochNumber previousEpoch
                  "nextEpoch" ==> epochNumber nextEpoch
                  "evidence" ==> evidence ]

        let result = prefixOwner?commit (stateView state, candidate)

        if unbox<bool> result?ok then
            Ok
                { (stateOf result?state) with
                    LastScopeArchive = state.LastScopeArchive
                    ScopeQuestions = state.ScopeQuestions }
        else
            Error(rejectionOf result)

    /// PERSIST-010 `ContextReanchored`: HOST-006 containment.
    let applyReanchor previousEpoch nextEpoch (observedRun: ProviderRunIdentity) state =
        commit previousEpoch nextEpoch (compaction observedRun) state

    let isReanchored (run: ProviderRunIdentity) (state: ActivePrefixEpoch) : bool =
        unbox<bool> (prefixOwner?isReanchored (stateView state, compaction run))

    /// Plan tenure handover: `TenureReanchored`.
    let applyTenureReanchor previousEpoch nextEpoch (incumbencyId: string) state =
        commit previousEpoch nextEpoch (tenure incumbencyId) state

    let isTenureReanchored (incumbencyId: string) (state: ActivePrefixEpoch) : bool =
        unbox<bool> (prefixOwner?isReanchored (stateView state, tenure incumbencyId))

    let applyScopeReanchor previousEpoch nextEpoch (callId: string) state =
        commit previousEpoch nextEpoch (scope callId) state

    let applyArchivedScopeReanchor
        previousEpoch
        nextEpoch
        (callId: string)
        (blob: BlobRef option)
        (digest: BlobDigest option)
        state
        =
        applyScopeReanchor previousEpoch nextEpoch callId state
        |> Result.map (fun advanced ->
            let captured =
                match blob, digest with
                | Some actual, Some checksum ->
                    Some
                        { SourceEpochId = previousEpoch
                          TargetEpochId = nextEpoch
                          ResetCallId = callId
                          ArchiveRef = actual
                          ArchiveDigest = checksum }
                | _ -> None

            { advanced with
                LastScopeArchive = captured
                ScopeQuestions = Map.empty })

    let scopeQuestionReceipt (callId: string) (state: ActivePrefixEpoch) = Map.tryFind callId state.ScopeQuestions

    let recordScopeQuestion
        (targetEpoch: PrefixEpochId)
        (callId: string)
        (digest: string)
        (state: ActivePrefixEpoch)
        : Result<ActivePrefixEpoch, string> =
        let validArchive =
            state.LastScopeArchive
            |> Option.exists (fun prior -> prior.TargetEpochId = targetEpoch)

        match targetEpoch = state.EpochId && validArchive, scopeQuestionReceipt callId state with
        | false, _ -> Error "stale scope epoch or missing prior archive"
        | true, Some earlier when earlier <> digest -> Error "scope question receipt conflict"
        | true, Some _ -> Ok state
        | true, None ->
            Ok
                { state with
                    ScopeQuestions = Map.add callId digest state.ScopeQuestions }

    let isScopeReanchored (callId: string) (state: ActivePrefixEpoch) : bool =
        unbox<bool> (prefixOwner?isReanchored (stateView state, scope callId))

    /// HOST-006: the one compaction among `observed` (oldest first) that still needs
    /// a reanchor line, with the epochs that line must carry.
    let proposeCompaction
        (observed: ProviderRunIdentity list)
        (state: ActivePrefixEpoch)
        : PrefixReanchorCandidate option =
        let runs = observed |> List.map ProviderRunIdentity.value |> List.toArray
        let candidate = prefixOwner?proposeCompaction (stateView state, runs)

        if isNull candidate then
            None
        else
            Some
                { PreviousEpochId = epochOf candidate?previousEpoch
                  NextEpochId = epochOf candidate?nextEpoch
                  CompactionRun = ProviderRunIdentity.create (string candidate?evidence?run) }

    /// Absorption policy owned by the library: `None` means the caller must write
    /// nothing (a replayed line or a repeated observation); `Some reason` is fatal.
    let describe (rejection: PrefixFoldRejection) : string option =
        match rejection with
        | PrefixFoldRejection.StalePrefixEpoch _
        | PrefixFoldRejection.CompactionAlreadyReanchored _
        | PrefixFoldRejection.ScopeAlreadyReanchored _
        | PrefixFoldRejection.TenureAlreadyReanchored _ -> None
        | PrefixFoldRejection.NonSequentialPrefixEpoch ->
            Some "prefix epoch is not the successor of the previous one (PERSIST-010)"
