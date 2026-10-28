namespace Wanxiangshu.Execution.Delegation.Handle

open System
open System.Threading.Tasks
open Wanxiangshu.Execution.Delegation
open Wanxiangshu.Execution.Delegation.Fork
open Wanxiangshu.Execution.Session
open Wanxiangshu.Foundation.Identity

module JoinDrain =

    /// The consequences one drain pass would deliver, in join order: every
    /// reportable abandonment and every completion awaiting join. The horizon
    /// roster reads this same list so it can never announce a return that join
    /// would not hand over, nor hide one it would.
    val orderedCandidates: projection: AgentLinkageProjection -> HandleRecord list

    val reconcileFalseAborts: durable: AgentJournalPort -> parentId: SessionId -> Task<Result<unit, ForkError>>

    val drainFromJournalWhere:
        durable: AgentJournalPort ->
        parentId: SessionId ->
        maxCount: int ->
        completedAt: DateTimeOffset ->
        accept: (HandleRecord -> bool) ->
            Task<Result<RunCompletion list, ForkError>>
