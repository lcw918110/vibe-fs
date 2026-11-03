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

type ActivePrefixEpoch = private { History: obj }

[<RequireQualifiedAccess>]
type PrefixFoldRejection =
    | StalePrefixEpoch of expected: PrefixEpochId * actual: PrefixEpochId
    | NonSequentialPrefixEpoch
    | CompactionAlreadyReanchored of run: ProviderRunIdentity
    | TenureAlreadyReanchored of incumbencyId: string
    | ScopeAlreadyReanchored of callId: string

type PrefixReanchorCandidate =
    { PreviousEpochId: PrefixEpochId
      NextEpochId: PrefixEpochId
      CompactionRun: ProviderRunIdentity }

module PrefixEpochProjection =
    [<Import("PrefixHistory", "wanxiangli/prefix")>]
    let private historyClass: obj = jsNative

    let empty: ActivePrefixEpoch = { History = createNew historyClass () }

    let ofGeneration (generation: obj) : ActivePrefixEpoch =
        { History = createNew historyClass generation }

    let private epochOf (value: obj) =
        PrefixEpochId.create (unbox<int64> (emitJsExpr value "BigInt($0)"))

    let epochId state = epochOf (state.History?epoch())
    let scopeCount state : int = state.History?scopeCount()
    let reanchoredScopes state : string array = state.History?reanchored "scope"
    let reanchoredTenures state : string array = state.History?reanchored "tenure"

    let reanchoredRuns state : ProviderRunIdentity array =
        unbox<string array> (state.History?reanchored "compaction")
        |> Array.map ProviderRunIdentity.create

    let lastScopeArchive state : ArchivedScope option =
        let value: obj = state.History?lastScopeArchive()

        if isNull value then
            None
        else
            Some
                { SourceEpochId = epochOf value?sourceEpoch
                  TargetEpochId = epochOf value?targetEpoch
                  ResetCallId = unbox value?resetCallId
                  ArchiveRef = BlobRef.create (unbox value?reference)
                  ArchiveDigest = BlobDigest.create (unbox value?digest) }

    let private compaction run =
        createObj [ "kind" ==> "compaction"; "run" ==> ProviderRunIdentity.value run ]

    let private tenure incumbencyId =
        createObj [ "kind" ==> "tenure"; "incumbencyId" ==> incumbencyId ]

    let private scope callId =
        createObj [ "kind" ==> "scope"; "callId" ==> callId ]

    let private candidate previousEpoch nextEpoch evidence =
        createObj
            [ "previousEpoch" ==> PrefixEpochId.value previousEpoch
              "nextEpoch" ==> PrefixEpochId.value nextEpoch
              "evidence" ==> evidence ]

    let private rejectionOf (result: obj) =
        let evidence = result?evidence

        match string result?error, (if isNull evidence then "" else string evidence?kind) with
        | "StaleEpoch", _ -> PrefixFoldRejection.StalePrefixEpoch(epochOf result?expected, epochOf result?actual)
        | "AlreadyReanchored", "tenure" -> PrefixFoldRejection.TenureAlreadyReanchored(string evidence?incumbencyId)
        | "AlreadyReanchored", "scope" -> PrefixFoldRejection.ScopeAlreadyReanchored(string evidence?callId)
        | "AlreadyReanchored", _ ->
            PrefixFoldRejection.CompactionAlreadyReanchored(ProviderRunIdentity.create (string evidence?run))
        | _ -> PrefixFoldRejection.NonSequentialPrefixEpoch

    let private resultOf (result: obj) =
        if unbox<bool> result?ok then
            Ok { History = result?value }
        else
            Error(rejectionOf result)

    let private commit previousEpoch nextEpoch evidence state =
        state.History?advance(candidate previousEpoch nextEpoch evidence) |> resultOf

    let applyReanchor previousEpoch nextEpoch observedRun state =
        commit previousEpoch nextEpoch (compaction observedRun) state

    let isReanchored run state : bool =
        state.History?hasReanchored(compaction run)

    let applyTenureReanchor previousEpoch nextEpoch (incumbencyId: string) state =
        commit previousEpoch nextEpoch (tenure incumbencyId) state

    let isTenureReanchored (incumbencyId: string) state : bool =
        state.History?hasReanchored(tenure incumbencyId)

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
        state.History?advanceScope(
            candidate previousEpoch nextEpoch (scope callId),
            blob |> Option.map BlobRef.value |> Option.toObj,
            digest |> Option.map BlobDigest.value |> Option.toObj
        )
        |> resultOf

    let scopeQuestionReceipt (callId: string) state : string option =
        let receipt: obj = state.History?scopeQuestion callId
        if isNull receipt then None else Some(unbox receipt)

    let recordScopeQuestion targetEpoch (callId: string) (digest: string) state : Result<ActivePrefixEpoch, string> =
        let result: obj =
            state.History?recordScopeQuestion(PrefixEpochId.value targetEpoch, callId, digest)

        if unbox<bool> result?ok then
            Ok { History = result?value }
        else
            Error(unbox result?error)

    let isScopeReanchored (callId: string) state : bool =
        state.History?hasReanchored(scope callId)

    let proposeCompaction (observed: ProviderRunIdentity list) state =
        let result: obj =
            state.History?proposeCompaction(observed |> List.map ProviderRunIdentity.value |> List.toArray)

        if isNull result then
            None
        else
            Some
                { PreviousEpochId = epochOf result?previousEpoch
                  NextEpochId = epochOf result?nextEpoch
                  CompactionRun = ProviderRunIdentity.create (string result?evidence?run) }

    let describe rejection =
        match rejection with
        | PrefixFoldRejection.StalePrefixEpoch _
        | PrefixFoldRejection.CompactionAlreadyReanchored _
        | PrefixFoldRejection.ScopeAlreadyReanchored _
        | PrefixFoldRejection.TenureAlreadyReanchored _ -> None
        | PrefixFoldRejection.NonSequentialPrefixEpoch ->
            Some "prefix epoch is not the successor of the previous one (PERSIST-010)"
