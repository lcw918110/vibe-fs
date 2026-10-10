namespace Wanxiangshu.Mission.Planning

open System.Threading.Tasks
open Wanxiangshu.Persistence.EventStore

type HandoffResult =
    { Ok: bool
      RetiredIncumbencyId: string
      NextIncumbencyId: string
      NextStage: string
      Message: string }

type DeliverResult =
    { Ok: bool
      Digest: string
      Path: string
      Message: string }

type AskResult =
    { Kind: string
      Question: string
      Status: string }

type ResumeResult =
    { Status: string
      Target: string
      Charge: string }

module PlanTools =
    val executeHandoff:
        store: IEventStore ->
        workId: string ->
        incumbencyId: string ->
        hasBlocker: bool ->
        note: string option ->
        retirementCursor: int64 option ->
            Task<Result<HandoffResult, string>>

    val executeDeliver:
        store: IEventStore ->
        root: string ->
        workId: string ->
        incumbencyId: string ->
        hasBlocker: bool ->
        note: string option ->
        retirementCursor: int64 option ->
            Task<Result<DeliverResult, string>>

    val executeAsk: store: IEventStore -> workId: string -> question: string -> Result<AskResult, string>

    val executeResume:
        store: IEventStore ->
        workId: string ->
        charge: string ->
        name: string option ->
            Task<Result<ResumeResult, string>>

    val executeJsPlan:
        root: string ->
        workId: string ->
        action: string ->
        content: string option ->
        patches: (string * string) list option ->
            Result<string, string>
