namespace Wanxiangshu.OpenCode

open System
open System.Collections.Generic
open System.Threading.Tasks
open Fable.Core
open Fable.Core.JsInterop
open Wanxiangshu.Execution.Failure
open Wanxiangshu.Foundation
open Wanxiangshu.Foundation.Identity
open Wanxiangshu.Foundation.HostSession
open Wanxiangshu.Foundation.TerminalSignal
open Wanxiangshu.Interaction.Authority
open Wanxiangshu.Interaction.Dispatch
open Wanxiangshu.Participant.Provider
open Wanxiangshu.Persistence.Journal
open Wanxiangshu.Process

[<AttachMembers>]
type OpenCodeRequestLatency(watchdog: LatencyWatchdog, isCompacting: string -> bool) =
    member _.ObserveEvent(raw: obj) =
        match HostEventCodec.tryDecodeExactProviderStart raw with
        | Some observation ->
            let session = SessionId.value observation.SessionId
            if not (isCompacting session) then
                watchdog.Begin(session, Some(ProviderRunIdentity.value observation.ProviderRun))
        | None -> ()

        match HostEventCodec.tryDecodeProviderStepEnd raw with
        | Some(session, _, run) ->
            let sessionId = SessionId.value session
            let providerRun = Some(ProviderRunIdentity.value run)
            match HostEventCodec.tryDecodeExactProviderTerminal raw with
            | Some observation ->
                match observation.Outcome with
                | Wanxiangshu.Execution.Session.ChatExecution.HostProviderTerminalOutcome.Completed _ ->
                    watchdog.Complete(sessionId, providerRun)
                | Wanxiangshu.Execution.Session.ChatExecution.HostProviderTerminalOutcome.Cancelled ExecutionFailure.UserCancelled ->
                    watchdog.OnProviderCancelled(sessionId, providerRun)
                | _ -> watchdog.Discard(sessionId, providerRun)
            | None -> watchdog.CompleteStep(sessionId, providerRun)
        | None -> ()

        match HostEventCodec.tryDecode raw with
        | Some(SessionIdle session) -> watchdog.OnIdle(SessionId.value session)
        | _ -> ()

    member _.ToolStarted(session: string) = watchdog.ToolStarted session
    member _.SessionDeleted(session: string) =
        watchdog.Cancel session
        watchdog.ForgetDetector session
    member _.Dispose() = watchdog.Dispose()

module RequestLatency =
    [<Import("requestLatencyDetector", "wanxiangli/degeneration")>]
    let private requestLatencyDetector: obj = jsNative

    [<Emit("performance.now()")>]
    let private performanceNow () : float = jsNative

    let create
        (sessionPort: ISessionHostPort)
        (rootWorkspace: IRootWorkspaceReader)
        (eventPort: IEventObservationPort)
        (journal: AgentJournal option)
        (isCompacting: string -> bool)
        : OpenCodeRequestLatency =
        let latencyTimerPort = NodeTiming.nodeTimerPort ()
        let latencyObservers = Dictionary<string, string * (unit -> unit)>()

        let latencyPorts: LatencyWatchdogPorts =
            { Now = performanceNow
              Schedule = fun milliseconds callback ->
                  let handle = latencyTimerPort.Delay(int milliseconds)

                  (task {
                      do! handle.Delay
                      callback ()
                  })
                  |> ignore<Task<unit>>

                  { new IDisposable with
                      member _.Dispose() = handle.Cancel() }
              Abort = fun sessionId ->
                  task {
                      let sid = SessionId.create sessionId
                      // Recorded before the Host acts: its Aborted terminal
                      // can arrive before this promise resolves.
                      Wanxiangshu.Foundation.TerminalSignal.DegenerationInterrupts.record sid
                      let! outcome = sessionPort.InterruptAttempt sid

                      return
                          match outcome with
                          | Ok() -> true
                          | Error _ ->
                              Wanxiangshu.Foundation.TerminalSignal.DegenerationInterrupts.unrecord sid
                              false
                  }
              Continue = fun sessionId prompt accepted onLateRefused ->
                  task {
                      // Detached is an actual handoff, not an Await with an
                      // artificial deadline. Physical acceptance owns the
                      // continuation outcome through the observer callback.
                      let registrations = ResizeArray<IDisposable>()
                      let token = Guid.NewGuid().ToString("N")
                      // DSL-MUTABLE: subscription — dispose this continuation's observers once
                      let mutable released = false
                      let release () =
                          if not released then
                              released <- true
                              match latencyObservers.TryGetValue sessionId with
                              | true, (current, _) when current = token ->
                                  latencyObservers.Remove sessionId |> ignore
                              | _ -> ()
                              for registration in registrations do
                                  registration.Dispose()
                      latencyObservers[sessionId] <- (token, release)

                      let acceptance: Wanxiangshu.Foundation.HostSession.ContinuationAcceptanceObserver =
                          { Notify = fun _ ->
                                release ()
                                accepted ()
                            AttachDisposable = fun registration ->
                                if released then registration.Dispose()
                                else registrations.Add registration
                            Budget = None }
                      try
                          let! sent =
                              HostSessionNudge.sendContinuationDetachedObservedResult
                                  sessionPort
                                  rootWorkspace
                                  (SessionId.create sessionId)
                                  prompt
                                  PromptAuthority.ContinuationKind.ProviderRetryAttempt
                                  None
                                  journal
                                  acceptance
                                  (fun reason ->
                                      release ()
                                      onLateRefused reason)

                          return
                              match sent with
                              | Ok _ -> ""
                              | Error reason ->
                                  release ()
                                  reason
                      with error ->
                          release ()
                          return error.Message
                  }
              DropObserver = fun sessionId ->
                  match latencyObservers.TryGetValue sessionId with
                  | true, (_, release) -> release ()
                  | _ -> ()
              Return = fun sessionId reason ->
                  let sid = SessionId.create sessionId
                  let text = "degeneration continuation unavailable: " + reason
                  // The active profile can close while the interrupt is in
                  // flight. The parent must still hear Failed: an
                  // authority-less session stop addresses its live run.
                  let stop =
                      if sessionPort.IsManagedChild sid then
                          match HostSessionNudge.tryActiveProfile journal sid with
                          | Some owner ->
                              Some(
                                  Wanxiangshu.Foundation.TerminalSignal.TerminalStop.forAuthority
                                      owner.AuthorityRootUserMessageId
                                      text
                              )
                          | None -> Some(Wanxiangshu.Foundation.TerminalSignal.TerminalStop.session text)
                      else
                          None
                  match stop with
                  | Some stop ->
                      if not (eventPort.NotifyTerminal
                                  sid
                                  (Wanxiangshu.Foundation.TerminalSignal.TerminalOutcome.Failed stop)) then
                          Diagnostic.emit
                              "latency-degeneration-return-unobserved"
                              [ "session_id", sessionId; "reason", reason ]
                  | None ->
                      Diagnostic.emit
                          "latency-degeneration-return-unowned"
                          [ "session_id", sessionId; "reason", reason ]
              Managed = fun sessionId ->
                  let sid = SessionId.create sessionId
                  sessionPort.IsManagedChild sid
                  && (HostSessionNudge.tryActiveProfile journal sid |> Option.isSome)
              Text = fun sessionId ->
                  ProviderProse.render
                      (ProviderLanguageBinding.forSessionText sessionId)
                      RuntimeNudge.LatencyDegeneration
                      Map.empty
              // CTX-014 only admits whitelisted fields; a non-whitelisted
              // name makes Diagnostic.emit drop the whole record.
              Diagnostic = fun kind reason -> Diagnostic.emit kind [ "reason", reason ] }

        let latencyWatchdog =
            new LatencyWatchdog(
                latencyPorts,
                { new RequestLatencyDetector with
                    member _.ThresholdMs sessionId =
                        unbox<float> (requestLatencyDetector?ThresholdMs sessionId)

                    member _.ObserveCompleted(sessionId, durationMs) =
                        requestLatencyDetector?ObserveCompleted (sessionId, durationMs) |> ignore

                    member _.ObserveCensored(sessionId, censoredMs) =
                        requestLatencyDetector?ObserveCensored (sessionId, censoredMs) |> ignore

                    member _.Forget sessionId =
                        requestLatencyDetector?Forget sessionId |> ignore }
            )

        OpenCodeRequestLatency(latencyWatchdog, isCompacting)
