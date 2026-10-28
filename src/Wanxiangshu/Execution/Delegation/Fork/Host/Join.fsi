namespace Wanxiangshu.Execution.Delegation.Fork.Host

open System.Threading.Tasks
open Wanxiangshu.Execution.Delegation
open Wanxiangshu.Execution.Delegation.Fork
open Wanxiangshu.Execution.Session
open Wanxiangshu.Execution.Session.Recovery.SessionRecovery
open Wanxiangshu.Execution.Session.Wait

module HostForkJoin =
    /// host-boundary-021: the current process's ownership cut over durable
    /// handles. Join admission and the horizon roster both answer through it.
    val currentProcessHandle: runtime: HostForkRuntime -> record: HandleRecord -> bool

    /// Whether this process is running a charge for the agent behind this
    /// handle — the run join would wait for.
    val hasPendingRun: runtime: HostForkRuntime -> handle: Wanxiangshu.Foundation.Identity.HandleId -> bool

    val joinAvailable:
        runtime: HostForkRuntime ->
        maxCount: int ->
        interrupt: Task<JoinInterruptReason> ->
            Task<Result<JoinWaitOutcome<JoinItem>, ForkError>>

    val joinAvailableForFissionLane:
        runtime: HostForkRuntime ->
        groupId: string ->
        laneIndex: int ->
        maxCount: int ->
        interrupt: Task<JoinInterruptReason> ->
            Task<Result<JoinWaitOutcome<JoinItem>, ForkError>>

    val joinAvailableWithPermit:
        runtime: HostForkRuntime ->
        permit: FamilyRecoveryPermit ->
        maxCount: int ->
        interrupt: Task<JoinInterruptReason> ->
            Task<Result<JoinWaitOutcome<JoinItem>, ForkError>>
