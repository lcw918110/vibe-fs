namespace Wanxiangshu.Host.Ports

open System.Threading.Tasks
open Wanxiangshu.Foundation.Identity
open Wanxiangshu.Foundation.Outcome

/// HNI P-02: deliver one user-role prompt, at most once.
type PromptRequest =
    {
        Text: string
        Route: ModelRoute option
        Role: AgentRole option
        Workspace: WorkspacePath option
        Correlation: PromptKey option
        ToolPolicy: Map<string, bool> option
        /// Host delivery verb chosen by the product. None keeps the host default.
        Delivery: string option
    }

/// Late transport verdict for a detached send. OwnedSettled: the returned task already settled
/// the send. Refused: never accepted. OutcomeUnknown: not observable, never resend.
[<RequireQualifiedAccess>]
type DeliveryVerdict =
    | OwnedSettled
    | Refused of reason: string
    | OutcomeUnknown of reason: string

type LateDeliveryListener = DeliveryVerdict -> Task

type IPromptPort =
    abstract Send: session: SessionId * request: PromptRequest * late: LateDeliveryListener option -> Task<SendOutcome>
