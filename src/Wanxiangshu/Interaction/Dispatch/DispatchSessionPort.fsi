namespace Wanxiangshu.Interaction.Dispatch

open System
open System.Threading.Tasks
open Wanxiangshu.Foundation.Identity
open Wanxiangshu.Foundation.Outcome
open Wanxiangshu.Foundation.HostSession
open Wanxiangshu.Foundation.TerminalSignal
open Wanxiangshu.Host.Ports

type IDispatchSessionPort =
    abstract SendPrompt:
        sessionId: SessionId * request: PromptRequest * metadata: obj option * late: LateDeliveryListener option ->
            Task<SendOutcome>

    abstract SubscribeFutureTerminal: sessionId: SessionId * listener: TerminalCompletionListener -> IDisposable
    abstract SubscribeTerminal: sessionId: SessionId * listener: TerminalCompletionListener -> IDisposable


[<RequireQualifiedAccess>]
module DispatchSessionPort =
    val toDeliveryVerdict: verdict: DetachedSendVerdict -> DeliveryVerdict
    val toDetachedVerdict: verdict: DeliveryVerdict -> DetachedSendVerdict

    val toHostOptions:
        request: PromptRequest -> metadata: obj option -> late: LateDeliveryListener option -> SessionPromptOptions

    val ofSessionPort: port: ISessionHostPort -> IDispatchSessionPort
    val forConsultation: port: IDispatchSessionPort -> IDispatchSessionPort
    val ofNeutral: promptPort: IPromptPort -> terminalSource: ISessionTerminalSource -> IDispatchSessionPort
