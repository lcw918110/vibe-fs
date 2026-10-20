namespace Wanxiangshu.Interaction.Repair

open System.Threading.Tasks
open Wanxiangshu.Foundation.Identity
open Wanxiangshu.Persistence.Journal

/// Durable InteractionRepair send (ENFORCER-066).
///
/// Wiring injects HostSessionNudge.trySendInteractionRepair with the session port
/// and root-workspace reader closed at the injection site.
[<RequireQualifiedAccess>]
type InteractionRepairSendOutcome =
    | Sent of PromptKey
    | AlreadyAdmitted
    | Retired
    | Failed of string

type InteractionRepairNudge =
    SessionId
        -> string
        -> string option
        -> AgentJournal option
        -> RepairRequestId
        -> ProviderRunIdentity
        -> string
        -> Task<InteractionRepairSendOutcome>
