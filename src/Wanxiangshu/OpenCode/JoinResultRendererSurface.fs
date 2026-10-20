namespace Wanxiangshu.OpenCode

open Wanxiangshu.Execution.Delegation.Fork.OpenCode
open Wanxiangshu.Execution.Session
open Wanxiangshu.Execution.Session.Wait
open Wanxiangshu.Foundation
open Wanxiangshu.Participant.Provider

/// Entry-local join rendering owner surface. It accepts a plain delivered view
/// and keeps join item unions/private non-empty batches inside the renderer.
module JoinResultRendererSurface =

    let renderAgentCompletion (language: string) (agentName: string) (view: string) : string =
        let providerLanguage = ProviderLanguage.parse language

        let payload: AgentCompletionPayload =
            { AgentId = agentName
              ChildSessionId = None
              RunId = "run-1"
              Role = Role.Coder
              AuthorityRoot = None
              ProviderRun = None
              View = view
              Directory = None }

        let batch =
            NonEmptyBatch.ofHeadTail (JoinItem.AgentItem(AgentJoinItem.AgentCompletedItem payload)) []

        JoinResultRenderer.renderJoinItemBatch providerLanguage (fun _ -> agentName) batch (fun _ -> agentName)
