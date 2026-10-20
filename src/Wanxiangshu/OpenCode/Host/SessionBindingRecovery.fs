namespace Wanxiangshu.OpenCode.Host

open Wanxiangshu.OpenCode

open Wanxiangshu.Composition.Durable
open Wanxiangshu.Execution.Delegation
open Wanxiangshu.Foundation.Identity
open Wanxiangshu.Persistence.Journal

open Wanxiangshu.Execution.Session
open Wanxiangshu.Execution.Fission

/// Durable topology evidence: which child session belongs to which parent, with
/// which execution agent, and which fission lane belongs to which owner. These
/// are pure reads of the durable projections — process-local tables are only a
/// cache of what this process currently drives, never the existence truth
/// (crash-reconciliation-020, managed-session-lifecycle-024).
module SessionBindingRecovery =

    let private agentNameOf (record: HandleRecord) : string =
        let target =
            if isNull record.TargetAgent then
                ""
            else
                record.TargetAgent.Trim()

        let byname = if isNull record.Byname then "" else record.Byname.Trim()
        // The execution agent is what the Host dispatches with (`engineer`), never
        // the logical reuse address (`decision-record-readonly`); binding the
        // byname would fail the next dispatch on participant drift.
        if not (System.String.IsNullOrWhiteSpace target) then
            target
        else
            byname

    let private parentOwningHandle (projections: AgentProjectionSet) (handle: HandleId) : SessionId option =
        projections.Sessions
        |> Map.toList
        |> List.tryPick (fun (parentSessionId, session) ->
            match session.Handles with
            | Some handles when HandleProjection.tryFind handle handles |> Option.isSome -> Some parentSessionId
            | _ -> None)

    /// Durable evidence for one child session: its parent and its execution agent.
    /// Only parent-visible durable handles answer; Host-owned hidden leaves stay
    /// invisible to the parent's binding surface.
    let evidenceFor (projections: AgentProjectionSet) (childSessionId: SessionId) : (string * string) option =
        // 1. Check parent-visible durable handles
        projections.HandleByChildSession
        |> Map.tryFind childSessionId
        |> Option.filter (fun record -> record.Ownership = HandleOwnership.DurableParentHandle)
        |> Option.map (fun record ->
            parentOwningHandle projections record.Handle
            |> Option.map (fun parentSessionId -> SessionId.value parentSessionId, agentNameOf record)
            |> Option.defaultWith (fun () ->
                // Fallback: child record exists in durable projection, resolve agent even if parent handle set is compacting
                "", agentNameOf record))
        |> Option.bind (fun (parentKey, agent) ->
            if System.String.IsNullOrWhiteSpace agent then
                None
            else
                Some(parentKey, agent))
        |> Option.orElseWith (fun () ->
            // 2. Check durable Companion session associations
            SessionAssociationProjection.tryMainSessionOf childSessionId projections.Associations
            |> Option.map (fun parentSessionId -> SessionId.value parentSessionId, "companion"))

    /// Durable evidence for one fission lane: which owner and slot it belongs to.
    /// The fission projection is the truth; the lane registry is the cache of the
    /// lanes this process is currently driving.
    let fissionLaneFor (projections: AgentProjectionSet) (laneSessionId: SessionId) : FissionLaneBinding option =
        let state = projections.Fission

        FissionProjection.tryMembershipOfLane laneSessionId state
        |> Option.bind (fun (group, laneIndex) ->
            FissionProjection.tryOwnerOfLane laneSessionId state
            |> Option.map (fun owner ->
                { GroupId = group.GroupId
                  OwnerSessionId = owner
                  LaneIndex = laneIndex
                  LaneCount = group.LaneCount }))

    /// Load Phase: install the durable fission-lane resolver behind the process
    /// lane registry. Child identity is read on demand by its own consumers, so
    /// there is nothing to pre-populate here.
    let install (journal: AgentJournal) : unit =
        let projections () =
            (AgentJournal.snapshot journal).AgentProjections

        FissionRuntime.installDurableLaneEvidence (fun laneSessionId -> fissionLaneFor (projections ()) laneSessionId)
