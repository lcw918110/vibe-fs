namespace Wanxiangshu.OpenCode.Tools

open System.Threading.Tasks
open Fable.Core
open Wanxiangshu.Persistence.Journal

[<AbstractClass; Sealed; AttachMembers>]
type ToolRuntimeScopeSurface =
    static member createAdvisorSender: journal: JournalHandle * sessionPort: obj -> obj

    static member sendBusyNudgeAwait:
        journal: JournalHandle * sessionPort: obj * session: string * prose: string * observation: obj -> Task<obj>

    static member evaluateRetirementBlockers: journal: JournalHandle * scenario: obj -> Task<string array>
    static member verifyDevOpsReturnDrain: journal: JournalHandle * scenario: obj -> Task<obj>
    static member verifyOwnershipRetirementContract: scenario: obj -> Task<obj>

    /// Read-back of the process-live delivery attempt ledger: the tool call id of the latest
    /// established delivery of this session on this physical input (the same read the transform
    /// hook's delivery adjudication uses), or None when nothing is established there.
    static member establishedDeliveryCallId: sessionId: string * physicalUserMessageId: string -> string option
