namespace Wanxiangshu.Interaction.Repair

open System.Threading.Tasks
open Wanxiangshu.Foundation.Identity
open Wanxiangshu.Persistence.Journal

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
