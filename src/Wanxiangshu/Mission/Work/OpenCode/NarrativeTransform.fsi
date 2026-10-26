namespace Wanxiangshu.Mission.Work.OpenCode

open System.Threading.Tasks
open Wanxiangshu.Foundation.Identity
open Wanxiangshu.Mission.Lifecycle
open Wanxiangshu.Persistence.Journal
open Wanxiangshu.Mission.Work

module RelayNarrativeTransform =
    val isStaleRetiredAttempt:
        journal: AgentJournal option ->
        acceptedRequest: bool ->
        sessionId: string option ->
        lifecycle: Wanxiangshu.Mission.Lifecycle.DurableWorkHost.InspectedWork option ->
        outObj: obj ->
            bool

    /// Messages the active successor may see. None leaves the array unchanged.
    val cutVisible:
        messages: obj array ->
        work: Wanxiangshu.Mission.Lifecycle.DurableWorkHost.InspectedWork option ->
            obj array option

    /// Model-visible messages for one committed work. The input array stays intact.
    val visibleMessages: stateRoot: string -> sessionId: string -> messages: obj -> obj

    val apply:
        journal: AgentJournal option ->
        acceptedRequest: bool ->
        interruptAttempt: (SessionId -> Task<unit>) ->
        sessionId: string option ->
        lifecycle: Wanxiangshu.Mission.Lifecycle.DurableWorkHost.InspectedWork option ->
        outObj: obj ->
            Task<RelayProjectionDisposition>

    /// 条款 4：本次请求不再放行时清空消息——LLM 看不到 deliver 回复。
    val clearRequestMessages: outObj: obj -> unit

    val RelayNarrativeTransform_apply:
        journal: AgentJournal option ->
        acceptedRequest: bool ->
        interruptAttempt: (SessionId -> Task<unit>) ->
        sessionId: string option ->
        lifecycle: Wanxiangshu.Mission.Lifecycle.DurableWorkHost.InspectedWork option ->
        outObj: obj ->
            Task<RelayProjectionDisposition>
