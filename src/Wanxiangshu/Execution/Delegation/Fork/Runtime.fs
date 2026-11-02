namespace Wanxiangshu.Execution.Delegation.Fork

open System
open System.Threading.Tasks
open Fable.Core
open Fable.Core.JsInterop
open Wanxiangshu.Execution.Session
open Wanxiangshu.Execution.Session.Wait
open Wanxiangshu.Foundation
open Wanxiangshu.Foundation.Identity

type ForkCompletionMailbox = ICompletionMailbox<AgentHandleId, PtyJoinItem, JoinInterruptReason, MailboxWakeReason>

type private IChildRunSet =
    abstract fork: obj -> string
    abstract restore: obj -> unit
    abstract bindChildSession: string * string -> unit
    abstract markInterrupted: string * string -> unit
    abstract cancelAgent: string -> unit
    abstract awaitAgent: string * int option -> Task<obj>
    abstract list: unit -> obj
    abstract registerPty: obj -> unit
    abstract unregisterPty: string -> unit
    abstract publishPtyCompletion: string * PtyJoinItem -> unit
    abstract pulseAgentHandle: string -> unit
    abstract pulseWake: unit -> unit
    abstract waitForWake: unit -> Task<MailboxWakeReason>
    abstract waitForSignal: Task<JoinInterruptReason> -> Task<MailboxWakeReason>
    abstract drainAgentWakes: int -> string array
    abstract drainPtyCompletions: int -> PtyJoinItem array
    abstract isCancelled: bool
    abstract activeRunCount: int
    abstract pendingCompletionCount: int
    abstract pendingPtyCount: int
    abstract close: unit -> unit

module private ChildRunValues =
    [<Import("ChildRunSet", "wanxiangli/handle-lifecycle")>]
    let library: obj = jsNative

    [<Emit("new $0($1)")>]
    let construct (_class: obj) (_options: obj) : IChildRunSet = jsNative

    [<Emit("$0 == null")>]
    let absent (_value: obj) : bool = jsNative

    let optional convert value =
        value |> Option.map (convert >> box) |> Option.defaultValue null

    let fromOptional convert value =
        if absent value then
            None
        else
            Some(convert (unbox<string> value))

    let role (value: obj) =
        Roles.tryParseRole (unbox value) |> Option.get

    let outcome =
        function
        | AgentCompleted value ->
            createObj
                [ "kind" ==> "completed"
                  "view" ==> value.View
                  "childSessionId" ==> optional SessionId.value value.ChildSessionId
                  "authorityRoot"
                  ==> optional AuthorityRootUserMessageId.value value.AuthorityRoot
                  "providerRun" ==> optional ProviderRunIdentity.value value.ProviderRun
                  "directory" ==> optional id value.Directory ]
        | AgentFailed value ->
            createObj
                [ "kind" ==> "failed"
                  "code" ==> value.Code
                  "message" ==> value.Message
                  "childSessionId" ==> optional SessionId.value value.ChildSessionId ]
        | AgentAbandoned(_, reason) -> createObj [ "kind" ==> "abandoned"; "reason" ==> reason ]

    let completion (value: obj) : RunCompletion =
        let data: obj = value?outcome

        let converted =
            match (data?kind: string) with
            | "completed" ->
                AgentCompleted
                    { AgentId = data?agentId
                      RunId = data?runId
                      Role = role data?role
                      ChildSessionId = fromOptional SessionId.create data?childSessionId
                      AuthorityRoot = fromOptional AuthorityRootUserMessageId.create data?authorityRoot
                      ProviderRun = fromOptional ProviderRunIdentity.create data?providerRun
                      View = data?view
                      Directory = fromOptional id data?directory }
            | "failed" ->
                AgentFailed
                    { AgentId = data?agentId
                      RunId = data?runId
                      Role = Some(role data?role)
                      ChildSessionId = fromOptional SessionId.create data?childSessionId
                      Code = data?code
                      Message = data?message }
            | _ -> AgentAbandoned(data?agentId, data?reason)

        { RunId = value?runId
          AgentName = value?agentName
          Role = role value?role
          Outcome = converted
          CompletedAt = value?completedAt }

    let agent (value: obj) : AgentRecord =
        let status =
            match (value?status: string) with
            | "Busy" -> AgentStatus.Busy
            | "Interrupted" -> AgentStatus.Interrupted
            | "Closed" -> AgentStatus.Closed
            | _ -> AgentStatus.Idle

        { AgentId = value?agentId
          Agent = value?agent
          Role = role value?role
          Status = status
          CurrentRunId = fromOptional id value?currentRunId
          TerminalStatusLabel = fromOptional id value?terminalStatusLabel
          CompletionCellSettled = value?completionCellSettled
          ChildSessionId = fromOptional SessionId.create value?childSessionId }

    let pty (value: obj) : PtyRecord =
        { PtyId = value?ptyId
          Command = value?command
          StartedAt = value?startedAt }

    let mailbox (value: ForkCompletionMailbox) =
        createObj
            [ "pulseAgent" ==> (fun (id: string) -> value.PulseAgent(AgentHandleId.create id))
              "publishPty" ==> (fun (item: obj) -> value.PublishPty(unbox item))
              "pulseWake" ==> (fun () -> value.PulseWake())
              "waitForWake" ==> (fun () -> value.WaitForWake())
              "waitForSignal"
              ==> (fun (interrupt: Task<JoinInterruptReason>) -> value.WaitForSignal interrupt)
              "drainAgents"
              ==> (fun (count: int) -> value.DrainAgents count |> List.map AgentHandleId.value |> List.toArray)
              "drainPtys" ==> (fun (count: int) -> value.DrainPtys count |> List.toArray)
              "cancel" ==> (fun () -> value.Cancel())
              "isCancelled" ==> (fun () -> value.IsCancelled)
              "pendingCount" ==> (fun () -> value.PendingCount)
              "pendingPtyCount" ==> (fun () -> value.PendingPtyCount) ]

type private ForkRuntimeBackendState
    (createMailbox: obj -> ForkCompletionMailbox, clock: IClockPort, raceExit: Task -> int -> Task<bool>) =
    let defaultRun (input: obj) =
        let prompt =
            if ChildRunValues.absent input?prompt then
                "ok"
            else
                (input?prompt: string)

        AgentCompletion.ofSimpleText (input?agentId) "run-local" (ChildRunValues.role input?role) prompt
        |> ChildRunValues.outcome
        |> Task.FromResult

    let native =
        ChildRunValues.construct
            ChildRunValues.library
            (createObj
                [ "mailbox" ==> ChildRunValues.mailbox (createMailbox (obj ()))
                  "now" ==> (fun () -> clock.UtcNow())
                  "mintRunId"
                  ==> (fun () -> "run-" + Guid.NewGuid().ToString("N").Substring(0, 8))
                  "raceExit" ==> (fun (pending: Task) (ms: int) -> raceExit pending ms)
                  "runDefault" ==> (fun (input: obj) -> defaultRun input) ])

    interface IForkRuntimeBackend with
        member _.Fork(agentId, role, agent, prompt, runWork) =
            let run =
                runWork
                |> Option.map (fun work ->
                    box (fun (_input: obj) ->
                        task {
                            let! value = work ()
                            return ChildRunValues.outcome value
                        }))
                |> Option.defaultValue null

            let result: string =
                native.fork (
                    createObj
                        [ "agentId" ==> agentId
                          "agent" ==> agent
                          "role" ==> Roles.roleLabel role
                          "prompt" ==> ChildRunValues.optional id prompt
                          "run" ==> run ]
                )

            match result with
            | "Created" -> ForkResult.Created agentId
            | "Nudged" -> ForkResult.Nudged agentId
            | _ -> ForkResult.NotFound agentId

        member _.WaitForSignal interrupt = native.waitForSignal interrupt
        member _.WaitForWake() = native.waitForWake ()
        member _.PulseWake() = native.pulseWake () |> ignore

        member _.PulseAgentHandle handle =
            native.pulseAgentHandle (AgentHandleId.value handle) |> ignore

        member _.PublishPtyCompletion item =
            native.publishPtyCompletion (PtyJoinItem.ptyId item, item) |> ignore

        member _.DrainAgentWakes maxCount =
            native.drainAgentWakes maxCount
            |> Array.map AgentHandleId.create
            |> Array.toList

        member _.DrainPtyCompletions maxCount =
            native.drainPtyCompletions maxCount |> Array.toList

        member _.RegisterPty pty =
            native.registerPty (
                createObj
                    [ "ptyId" ==> pty.PtyId
                      "command" ==> pty.Command
                      "startedAt" ==> pty.StartedAt ]
            )
            |> ignore

        member _.UnregisterPty ptyId = native.unregisterPty (ptyId) |> ignore

        member _.Restore(agentId, role, agent) =
            native.restore (createObj [ "agentId" ==> agentId; "agent" ==> agent; "role" ==> Roles.roleLabel role ])
            |> ignore

        member _.MarkInterrupted(agentId, reason) =
            native.markInterrupted (agentId, reason) |> ignore

        member _.BindChildSession(agentId, childSessionId) =
            native.bindChildSession (agentId, SessionId.value childSessionId) |> ignore

        member _.AwaitAgent(agentId, timeoutMs) =
            task {
                let! value = native.awaitAgent (agentId, timeoutMs)

                return
                    if (value?ok: bool) then
                        Ok(ChildRunValues.completion value?value)
                    else
                        Error(value?error)
            }

        member _.CancelAgent agentId = native.cancelAgent (agentId) |> ignore

        member _.List() =
            let value = native.list ()

            ((value?agents: obj array) |> Array.map ChildRunValues.agent |> Array.toList,
             (value?ptys: obj array) |> Array.map ChildRunValues.pty |> Array.toList)

        member _.IsCancelled = native.isCancelled
        member _.ActiveRunCount = native.activeRunCount
        member _.PendingCompletionCount = native.pendingCompletionCount
        member _.PendingPtyCount = native.pendingPtyCount
        member _.Cancel() = native.close () |> ignore

[<RequireQualifiedAccess>]
module ForkRuntimeBackend =
    let create
        (clock: IClockPort)
        (raceExit: Task -> int -> Task<bool>)
        (createMailbox: obj -> ForkCompletionMailbox)
        : ForkRuntime =
        ForkRuntime(backend = (ForkRuntimeBackendState(createMailbox, clock, raceExit) :> IForkRuntimeBackend))
