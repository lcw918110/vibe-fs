namespace Wanxiangshu.Mission.Planning

open Wanxiangshu.Context.Trace

type ActiveTenureInfo =
    { WorkId: string
      IncumbencyId: string
      Stage: string
      OpeningCursor: int64
      PreviousRange: (int64 * int64) option
      IsFreshHandover: bool }

type TenureMessage =
    { Id: string option
      Role: string
      Content: string
      Cursor: int64 option
      Raw: obj }

type TenureAssemblyResult =
    { Messages: obj list
      ReanchorRequested: bool }

module TenureIsolation =
    val messageOfRaw: raw: obj -> TenureMessage
    val tenureOfRaw: raw: obj -> ActiveTenureInfo
    val assembleTenureMessages: rawMessagesInput: obj -> tenureInput: obj -> materializePrev: (obj -> string) -> obj
    val assembleAskContinuation: rawMessagesInput: obj -> pendingAskObj: obj -> tenureInput: obj -> obj
