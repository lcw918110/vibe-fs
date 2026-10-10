namespace Wanxiangshu.Interaction.Attention

open Wanxiangshu.Foundation.Identity

[<RequireQualifiedAccess>]
module AttentionSurface =

    type private BoxedState(state: AttentionProjectionState) =
        member _.State = state

    let private stateOf (value: obj) = (unbox<BoxedState> value).State
    let internal projection (state: obj) = stateOf state
    let private boxed state = BoxedState(state) :> obj

    let empty () = boxed AttentionProjection.empty

    let record (session: string) (occurrence: string) (text: string) (state: obj) =
        stateOf state
        |> AttentionProjection.record (SessionId.create session) occurrence text
        |> boxed

    let consume (session: string) (workIds: string array) (state: obj) =
        stateOf state
        |> AttentionProjection.consume (SessionId.create session) (Array.toList workIds)
        |> boxed

    /// ATTENTION-004: close one life, leaving its remaining work as
    /// consumption receipts so replay cannot resurrect it.
    let closeLife (session: string) (state: obj) =
        stateOf state
        |> AttentionProjection.closeLife (SessionId.create session)
        |> boxed

    let pending (session: string) (state: obj) : obj =
        stateOf state
        |> AttentionProjection.pending (SessionId.create session)
        |> List.map (fun item ->
            box
                {| occurrence = item.OccurrenceId
                   text = item.Text |})
        |> List.toArray
        |> box
