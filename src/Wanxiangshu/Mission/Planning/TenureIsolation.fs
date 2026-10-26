namespace Wanxiangshu.Mission.Planning

type ActiveTenureInfo =
    { WorkId: string
      IncumbencyId: string
      Stage: string
      OpeningCursor: int64
      IsFreshHandover: bool
      BarrierToolCallId: string option
      SuccessorTenureId: string option }

type TenureMessage =
    { Id: string option
      Role: string
      IsSynthetic: bool
      HasSemanticParts: bool
      Raw: obj }

type TenureAssemblyResult =
    { Messages: obj list
      ReanchorRequested: bool }

module TenureIsolation =

    open System
    open Fable.Core
    open Fable.Core.JsInterop

    /// 任期过滤规则的唯一实现：wanxiangli/projection。
    [<AllowNullLiteral>]
    type private ProjectionMessages =
        abstract tenure: messages: obj * barrier: obj -> obj

    [<Import("Projection", "wanxiangli/projection")>]
    let private createProjection: unit -> ProjectionMessages = jsNative

    let private projection = createProjection ()

    let private physicalCursor (messageCursors: Map<string, int64>) (message: TenureMessage) =
        message.Id
        |> Option.bind (fun messageId -> Map.tryFind messageId messageCursors)

    let private presentText (raw: obj) (name: string) : obj =
        let value = raw?(name)

        if isNull value || jsTypeof value <> "string" || String.IsNullOrEmpty(unbox<string> value) then
            null
        else
            value

    let private neutral (messageCursors: Map<string, int64>) (message: TenureMessage) : obj =
        createObj
            [ "id" ==> Option.toObj message.Id
              "role" ==> message.Role
              "synthetic" ==> message.IsSynthetic
              "semantic" ==> message.HasSemanticParts
              "cursor"
              ==> (match physicalCursor messageCursors message with
                   | Some cursor -> box cursor
                   | None -> null)
              "toolCallId" ==> presentText message.Raw "toolCallId"
              "resultFor" ==> presentText message.Raw "resultFor"
              "tenureId" ==> presentText message.Raw "tenureId" ]

    /// No committed barrier leaves the decoded rows in place.
    /// A barrier hides the triggering review and everything before it,
    /// then keeps only the successor's non-synthetic rows.
    let assemble
        (messages: TenureMessage list)
        (tenure: ActiveTenureInfo)
        (messageCursors: Map<string, int64>)
        : TenureAssemblyResult =
        let rows = List.toArray messages

        let selected =
            match tenure.BarrierToolCallId, tenure.SuccessorTenureId with
            | Some callId, Some successor ->
                let barrier =
                    createObj
                        [ "toolCallId" ==> callId
                          "successorTenureId" ==> successor ]

                let kept =
                    projection.tenure (rows |> Array.map (neutral messageCursors) |> box, barrier)

                unbox<obj array> kept
                |> Array.map (fun index -> rows.[int (unbox<float> index)].Raw)
                |> Array.toList
            | _ -> messages |> List.map (fun message -> message.Raw)

        { Messages = selected
          ReanchorRequested = tenure.IsFreshHandover }
