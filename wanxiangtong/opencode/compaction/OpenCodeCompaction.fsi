namespace Wanxiangshu.OpenCode

open System.Threading.Tasks
open Fable.Core
open Wanxiangshu.Foundation.HostSession
open Wanxiangshu.Foundation.Identity
open Wanxiangshu.Host
open Wanxiangshu.Host.Ports
open Wanxiangshu.Persistence.Journal

[<AttachMembers>]
type OpenCodeCompaction =
    new: journal: AgentJournal option -> OpenCodeCompaction
    member RecordSettingGap: gap: CompactionSetting option -> unit
    member IsCompacting: session: string -> bool
    member TemporaryOverflowed: session: string -> bool
    member FinishTemporary: session: string -> unit
    member ForgetSession: session: string -> unit
    member TryConsumeHistoryBypass: input: obj * output: obj -> bool
    member OnCompacting: product: ICompactionGate * isTemporary: (string -> bool) * client: obj * input: obj * output: obj -> Task<unit>
    member OnAutoContinue: product: ICompactionGate * isTemporary: (string -> bool) * input: obj * output: obj -> Task<unit>
    member ObserveSnapshot: session: SessionId * messages: SessionMessage list -> Task
