namespace Wanxiangshu.Execution.Routing

open System
open System.Collections.Generic
open Fable.Core
open Fable.Core.JsInterop
open Wanxiangshu.Foundation

/// The slice of the `wanxiangli/permit` exact execution permit owner (CUT-10)
/// this host owner reads. Issue, the Pending → Committed → Released lifecycle,
/// the two mutually exclusive releases, exact identity checks and the
/// outcome classification are decided there; this adapter keeps the F#
/// lease handle, maps outcomes and records the shared transition counters.
type internal ExecutionPermitLibrary =
    abstract issue: identity: obj * credit: int64 -> obj
    abstract target: permit: obj * fence: int64 -> obj
    abstract commit: permit: obj * fence: int64 * observed: obj -> string
    abstract releaseBeforeProvider: permit: obj * fence: int64 * observed: obj -> string
    abstract releasePhysical: sessionId: string * physicalUserMessageId: string -> string
    abstract lifecycle: permit: obj * fence: int64 -> obj
    abstract committed: sessionId: string * physicalUserMessageId: string -> obj

type internal SessionLeaseHandlesLibrary =
    abstract handleFor: sessionId: string * permit: obj * createHandle: Func<obj, obj, obj> -> obj
    abstract tryGetMatching: sessionId: string * permit: obj -> obj

module internal ExecutionPermitBinding =
    [<Import("ExecutionPermits", "wanxiangli/permit")>]
    let private permitsClass: obj = jsNative

    [<Import("SessionLeaseHandles", "wanxiangli/permit")>]
    let private sessionLeaseHandlesClass: obj = jsNative

    [<Emit("new $0()")>]
    let private constructPermits (cls: obj) : ExecutionPermitLibrary = jsNative

    [<Emit("new $0()")>]
    let private constructHandles (cls: obj) : SessionLeaseHandlesLibrary = jsNative

    let create () = constructPermits permitsClass

    let createHandles () =
        constructHandles sessionLeaseHandlesClass

    let identity (value: ExecutionAdmissionExactIdentity) : obj =
        createObj
            [ "sessionId" ==> value.SessionId
              "physicalUserMessageId" ==> value.PhysicalUserMessageId
              "role" ==> Roles.roleLabel value.Role
              "participant" ==> value.Participant
              "target"
              ==> createObj [ "model" ==> value.Target.Model; "reasoning" ==> value.Target.Reasoning ] ]

    let outcome (value: string) =
        match value with
        | "Applied" -> CapacityTransitionOutcome.Applied
        | "AlreadyApplied" -> CapacityTransitionOutcome.AlreadyApplied
        | "StaleFence" -> CapacityTransitionOutcome.StaleFence
        | "Conflict" -> CapacityTransitionOutcome.Conflict
        | other -> invalidOp (sprintf "execution-model-routing: unknown permit outcome '%s'" other)

    let rejection (value: string) =
        match value with
        | "UnknownLease" -> ExecutionAdmissionRejection.UnknownLease
        | "WrongFence" -> ExecutionAdmissionRejection.WrongFence
        | "StaleLease" -> ExecutionAdmissionRejection.StaleLease
        | "WrongSession" -> ExecutionAdmissionRejection.WrongSession
        | "WrongPhysicalUserMessage" -> ExecutionAdmissionRejection.WrongPhysicalUserMessage
        | "WrongRole" -> ExecutionAdmissionRejection.WrongRole
        | "WrongParticipant" -> ExecutionAdmissionRejection.WrongParticipant
        | "WrongTarget" -> ExecutionAdmissionRejection.WrongTarget
        | "IllegalTransition" -> ExecutionAdmissionRejection.IllegalTransition
        | "OppositeTerminalConflict" -> ExecutionAdmissionRejection.OppositeTerminalConflict
        | other -> invalidOp (sprintf "execution-model-routing: unknown permit rejection '%s'" other)

    let result (value: obj) : Result<obj, ExecutionAdmissionRejection> =
        if unbox<bool> value?ok then
            Ok(value?value: obj)
        else
            Error(rejection (unbox<string> value?rejection))

    [<Emit("{ tag: 0, fields: [BigInt($0)] }")>]
    let leaseIdOf (value: obj) : CapacityLeaseId = jsNative

    [<Emit("{ tag: 0, fields: [BigInt($0)] }")>]
    let fenceOf (value: obj) : CapacityFence = jsNative

type internal ExecutionCapacityOwner(counters: CapacityTransitionCounters) =
    let permits = ExecutionPermitBinding.create ()
    let sessionLeases = ExecutionPermitBinding.createHandles ()

    let permitOf (lease: ExecutionAdmissionLease) : obj =
        if isNull (box lease) then null else lease.Owner

    let fenceOf (lease: ExecutionAdmissionLease) : int64 =
        if isNull (box lease) then
            0L
        else
            CapacityFence.value lease.Fence

    let record (outcome: string) =
        outcome |> ExecutionPermitBinding.outcome |> counters.Record

    let createHandle (identity: ExecutionAdmissionExactIdentity) (capacityCredit: CapacityCreditId) (permit: obj) =
        Func<obj, obj, obj>(fun leaseId fence ->
            let lid = ExecutionPermitBinding.leaseIdOf leaseId
            let f = ExecutionPermitBinding.fenceOf fence
            ExecutionAdmissionLease.Create(permit, capacityCredit, lid, f, identity) :> obj)

    member _.Issue(identity: ExecutionAdmissionExactIdentity, capacityCredit: CapacityCreditId) =
        let permit =
            permits.issue (ExecutionPermitBinding.identity identity, CapacityCreditId.value capacityCredit)

        sessionLeases.handleFor (identity.SessionId, permit, createHandle identity capacityCredit permit)
        |> unbox<ExecutionAdmissionLease>

    member _.Target(lease: ExecutionAdmissionLease) =
        permits.target (permitOf lease, fenceOf lease)
        |> ExecutionPermitBinding.result
        |> Result.map (fun _ -> lease.Identity.Target)

    member _.Commit(lease: ExecutionAdmissionLease, observed: ExecutionAdmissionExactIdentity) =
        permits.commit (permitOf lease, fenceOf lease, ExecutionPermitBinding.identity observed)
        |> record

    member _.ReleaseBeforeProvider(lease: ExecutionAdmissionLease, observed: ExecutionAdmissionExactIdentity) =
        permits.releaseBeforeProvider (permitOf lease, fenceOf lease, ExecutionPermitBinding.identity observed)
        |> record

    member _.ReleasePhysical(sessionId: string, physicalUserMessageId: string) =
        permits.releasePhysical (sessionId, physicalUserMessageId) |> record

    member _.LifecycleName(lease: ExecutionAdmissionLease) =
        permits.lifecycle (permitOf lease, fenceOf lease)
        |> ExecutionPermitBinding.result
        |> Result.map unbox<string>

    /// Read-only exact committed lease query; a pending, releasing, released,
    /// absent or wrong-physical lease is not an executable resource.
    member _.TryReadCommittedLease(sessionId: string, physicalUserMessageId: string) =
        let permit = permits.committed (sessionId, physicalUserMessageId)
        let handle = sessionLeases.tryGetMatching (sessionId, permit)

        match isNull permit, isNull handle with
        | true, _ -> None
        | false, true -> invalidOp "execution-model-routing: committed permit has no host lease handle"
        | false, false -> Some(unbox<ExecutionAdmissionLease> handle)
