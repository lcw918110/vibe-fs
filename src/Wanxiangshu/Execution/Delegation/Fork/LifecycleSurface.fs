namespace Wanxiangshu.Execution.Delegation.Fork

open System
open Wanxiangshu.Execution.Session
open Wanxiangshu.Foundation
open Wanxiangshu.Foundation.Identity

/// JS-native lifecycle snapshots for the delegation-owned child run. The
/// completion cell and cancellation token remain opaque runtime resources.
module ForkLifecycleSurface =
    let private epoch = DateTimeOffset(1970, 1, 1, 0, 0, 0, TimeSpan.Zero)

    let snapshot (action: string) (runtimeCancelled: bool) (message: string) : obj =
        let run = ChildRun.create "agent-1" "run-1" "coder" Role.Manager "prompt" epoch

        if runtimeCancelled || action = "cancel" then
            ChildRun.cancel run

        let cancelled = ChildRun.isCancelled run

        let status, active, completed, label =
            if action = "complete" then
                let payload =
                    { AgentId = "agent-1"
                      ChildSessionId = None
                      RunId = "run-1"
                      Role = Role.Manager
                      AuthorityRoot = None
                      ProviderRun = None
                      View = "completed"
                      Directory = None }

                let completion =
                    ChildRun.makeCompleted run (AgentCompletionOutcome.AgentCompleted payload) epoch

                ChildRun.tryComplete run completion |> ignore
                "Idle", false, true, "completed"
            elif action = "fail" || action = "interrupt" || action = "abandon" then
                let completion = ChildRun.makeFailed run message epoch
                ChildRun.tryComplete run completion |> ignore

                (if action = "interrupt" then "Interrupted"
                 else if action = "abandon" then "Closed"
                 else "Idle"),
                false,
                true,
                message
            elif cancelled then
                "Closed", false, ChildRun.isCompleted run, ""
            else
                "Busy", ChildRun.isActive run, ChildRun.isCompleted run, ""

        box
            {| agentId = run.AgentId
               agent = run.AgentName
               role = run.Role.ToString()
               runId = run.RunId
               childSession = null
               status = status
               currentRunId = if active then run.RunId else null
               terminalStatusLabel = if String.IsNullOrWhiteSpace label then null else label
               completionCellSettled = completed
               active = active
               completed = completed
               cancelled = cancelled |}
