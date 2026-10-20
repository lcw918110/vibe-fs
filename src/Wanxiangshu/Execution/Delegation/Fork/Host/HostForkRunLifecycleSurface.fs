namespace Wanxiangshu.Execution.Delegation.Fork.Host

open System
open System.Collections.Generic
open System.Threading.Tasks
open Fable.Core.JsInterop
open Wanxiangshu.Context.Trace
open Wanxiangshu.Execution.Session
open Wanxiangshu.Foundation
open Wanxiangshu.Foundation.Identity
open Wanxiangshu.OpenCode

/// JS-native owner boundary for one HostForkRunLifecycle pending run.
///
/// The Host lifecycle remains the sole owner of terminal admission. A caller
/// supplies plain outcome observations and receives an opaque run capability;
/// PendingHostRun, TerminalOutcome, AgentRunResult, the completion source, and
/// the pending-runs dictionary never cross this boundary.
type HostForkRunLifecycleHandle
    private (gate: obj, pendingRuns: Dictionary<string, PendingHostRun>, parentId: SessionId, run: PendingHostRun) =

    member _.Gate = gate
    member _.PendingRuns = pendingRuns
    member _.ParentId = parentId
    member _.Run = run

    static member Create(agentId: string, childId: string, parentId: string) : HostForkRunLifecycleHandle =
        let run =
            { Token = obj ()
              AgentId = agentId
              ChildId = SessionId.create childId
              Role = Role.Coder
              AuthorityRoot = AuthorityRootUserMessageId.create "surface-authority-root"
              Work = None
              Source = HostPendingRun.completionSource ()
              Subscription = None
              Finished = false }

        // DSL-MUTABLE: resource — pending host run registry by agent id
        let pendingRuns = Dictionary<string, PendingHostRun>()
        pendingRuns.[agentId] <- run
        HostForkRunLifecycleHandle(obj (), pendingRuns, SessionId.create parentId, run)

[<RequireQualifiedAccess>]
module HostForkRunLifecycleSurface =

    let private property (value: obj) (name: string) : obj = emitJsExpr (value, name) "$0[$1]"

    let private text (value: obj) =
        if isNull value then "" else string value

    let private detail (value: obj) (fieldName: string) =
        let candidate = property value fieldName

        if isNull candidate then
            text (property value "text")
        else
            text candidate

    let private terminal (handle: HostForkRunLifecycleHandle) (value: obj) : TerminalOutcome =
        let kind = text (property value "kind")

        match kind with
        | "Completed" ->
            let terminalText = detail value "terminalText"

            TerminalOutcome.Completed
                { SessionId = handle.Run.ChildId
                  AuthorityRootUserMessageId =
                    match property value "authorityRoot" with
                    | null -> handle.Run.AuthorityRoot
                    | root -> AuthorityRootUserMessageId.create (string root)
                  ProviderRun = ProviderRunIdentity.create ("run-" + handle.Run.AgentId)
                  Role = handle.Run.Role
                  Directory = None
                  TerminalText = terminalText
                  TurnFormalText = terminalText }
        | "Aborted" -> TerminalOutcome.Aborted(TerminalStop.session (detail value "reason"))
        | "Failed" -> TerminalOutcome.Failed(TerminalStop.session (detail value "message"))
        | other -> invalidArg "outcome" (sprintf "unknown terminal outcome: %s" other)

    let private completionView (outcome: AgentCompletionOutcome) : obj =
        match outcome with
        | AgentCompleted payload ->
            box
                {| status = "completed"
                   agentId = payload.AgentId
                   view = payload.View |}
        | AgentFailed payload ->
            box
                {| status = "failed"
                   agentId = payload.AgentId
                   code = payload.Code
                   message = payload.Message |}
        | AgentAbandoned(agentId, reason) ->
            box
                {| status = "abandoned"
                   agentId = agentId
                   reason = reason |}

    /// Create one pending run. All lifecycle state is retained by the opaque handle.
    let create (value: obj) : obj =
        let agentId = text (property value "agentId")
        let childId = text (property value "childId")
        let parentId = text (property value "parentId")
        HostForkRunLifecycleHandle.Create(agentId, childId, parentId) :> obj

    /// Feed one plain terminal observation through the production lifecycle owner.
    let complete (handle: obj) (outcome: obj) : Task =
        let typed = handle :?> HostForkRunLifecycleHandle

        HostForkRunLifecycle.complete
            typed.Gate
            typed.PendingRuns
            None
            typed.ParentId
            Unchecked.defaultof<ISessionHostPort>
            typed.Run
            (terminal typed outcome)
            None

    /// Read only lifecycle facts needed by the semantic contract.
    let observe (handle: obj) : obj =
        let typed = handle :?> HostForkRunLifecycleHandle

        box
            {| agentId = typed.Run.AgentId
               pending = typed.PendingRuns.ContainsKey typed.Run.AgentId
               finished = typed.Run.Finished |}

    /// Await the owner completion cell; the result is a plain status observation.
    let completion (handle: obj) : Task<obj> =
        let typed = handle :?> HostForkRunLifecycleHandle

        task {
            let! outcome = typed.Run.Source.Task
            return completionView outcome
        }
