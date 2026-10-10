namespace Wanxiangshu.Interaction.Attention

open Wanxiangshu.Foundation.Identity

type DeferredWorkItem = { OccurrenceId: string; Text: string }

type AttentionProjectionState =
    { BySession: Map<SessionId, DeferredWorkItem list>
      ConsumedBySession: Map<SessionId, Set<string>> }

[<RequireQualifiedAccess>]
module AttentionProjection =

    let empty =
        { BySession = Map.empty
          ConsumedBySession = Map.empty }

    let private items sessionId state =
        Map.tryFind sessionId state.BySession |> Option.defaultValue []

    let pending sessionId state = items sessionId state

    let tryFind sessionId occurrenceId state =
        items sessionId state
        |> List.tryFind (fun item -> item.OccurrenceId = occurrenceId)

    /// ATTENTION-004/006: a consumed occurrence leaves a consumption receipt
    /// in the projection, so a replayed record cannot resurrect it.
    let wasConsumed sessionId occurrenceId state =
        match Map.tryFind sessionId state.ConsumedBySession with
        | Some consumed -> Set.contains occurrenceId consumed
        | None -> false

    let record sessionId occurrenceId text state =
        let current = items sessionId state

        let alreadyKnown =
            current |> List.exists (fun item -> item.OccurrenceId = occurrenceId)
            || wasConsumed sessionId occurrenceId state

        if alreadyKnown then
            state
        else
            { state with
                BySession =
                    Map.add
                        sessionId
                        (current
                         @ [ { OccurrenceId = occurrenceId
                               Text = text } ])
                        state.BySession }

    /// Consume (and thereby extinguish) the named DeferredWork occurrences.
    /// Every named occurrence leaves a consumption receipt, present or not:
    /// a receipt for an occurrence that is still absent is a no-op today, and
    /// a receipt replayed before its `DeferredWorkRecorded` still suppresses
    /// the record. Consumption is therefore order-independent across k-way
    /// journal replay and repeated consumption stays idempotent.
    let consume sessionId workIds state =
        let selected = Set.ofList workIds

        let remaining =
            items sessionId state
            |> List.filter (fun item -> not (Set.contains item.OccurrenceId selected))

        let consumed =
            Map.tryFind sessionId state.ConsumedBySession
            |> Option.defaultValue Set.empty
            |> Set.union selected

        { state with
            BySession = Map.add sessionId remaining state.BySession
            ConsumedBySession = Map.add sessionId consumed state.ConsumedBySession }

    /// ATTENTION-004/005: a life that ends before consumption takes its
    /// remaining entries with it — a reused SessionId starts a fresh life and
    /// must not inherit the closed life's pending work. The remaining ids stay
    /// as consumption receipts, so a replayed `DeferredWorkRecorded` cannot
    /// resurrect them.
    let closeLife sessionId state =
        let pending = items sessionId state

        let consumed =
            Map.tryFind sessionId state.ConsumedBySession
            |> Option.defaultValue Set.empty
            |> Set.union (pending |> List.map (fun item -> item.OccurrenceId) |> Set.ofList)

        { state with
            BySession = Map.remove sessionId state.BySession
            ConsumedBySession = Map.add sessionId consumed state.ConsumedBySession }
