namespace Wanxiangshu.Interaction.Authority

/// The closed set of closure kinds and the agent-owner life labels, mirroring
/// the canonical law in `wanxiangli/src/authority/ClosureLaw.fs`.
///
/// The host records the expected closure kind of an accepted authority root
/// atomically with the acceptance (interaction-authority-018 / C1 closure), so
/// the law is mirrored here as a pure module rather than reached through the
/// JavaScript bridge: the bridge only answers for already-rooted lifecycles,
/// while the host must be able to derive the kind *before* the root exists.
[<RequireQualifiedAccess>]
module AuthorityLifeClassification =
    /// The closed set of expected closure kinds.
    let closureKindLabels =
        [ "HumanRootManagerLife"
          "HumanRootManagedRun"
          "AgentOwnerChildWork"
          "AgentOwnerAttachedWork"
          "AgentOwnerInternalLeaf" ]

    /// The closed set of agent-owner life labels.
    let agentOwnerLifeLabels = [ "Child"; "Attached"; "InternalLeaf" ]

    /// The root kind label of a closure kind: "HumanRoot" | "AgentOwnerRoot".
    let rootKindOfKind (closureKind: string) : string =
        match closureKind with
        | "HumanRootManagerLife"
        | "HumanRootManagedRun" -> "HumanRoot"
        | "AgentOwnerChildWork"
        | "AgentOwnerAttachedWork"
        | "AgentOwnerInternalLeaf" -> "AgentOwnerRoot"
        | other -> invalidArg "closureKind" (sprintf "unknown closure kind '%s'" other)

    /// The closure witness kind a closure of this kind must carry.
    ///
    /// A Manager life closes with a life witness; every other closure kind
    /// closes with a work witness.
    let witnessKindOf (closureKind: string) : string option =
        match closureKind with
        | "HumanRootManagerLife" -> Some "LifeCompleted"
        | "HumanRootManagedRun"
        | "AgentOwnerChildWork"
        | "AgentOwnerAttachedWork"
        | "AgentOwnerInternalLeaf" -> Some "WorkCompleted"
        | _ -> None

    /// The coarse agent-owner life used when the host cannot observe the life
    /// of an agent-owner root at acceptance time.
    ///
    /// The durable session-ownership projection does not distinguish the three
    /// agent-owner lives (child, attached, internal leaf) at the moment of
    /// acceptance. Rather than record no kind at all — which would admit no
    /// closure for the root — the coarse default keeps the acceptance atomic
    /// and the closure admitted, at the cost of losing the life distinction.
    let coarseAgentOwnerLife = "Child"

    /// The closure kind this root acceptance expects, mirroring
    /// `ClosureLaw.expectedKind` exactly.
    ///
    /// Returns None when the inputs cannot determine a unique kind — in
    /// particular an agent-owner root without a life, which must fail closed
    /// rather than close under a guessed kind.
    let expectedClosureKind (rootKindLabel: string) (isManager: bool) (agentOwnerLife: string option) : string option =
        match rootKindLabel, agentOwnerLife with
        | "HumanRoot", None when isManager -> Some "HumanRootManagerLife"
        | "HumanRoot", None -> Some "HumanRootManagedRun"
        | "HumanRoot", Some _ -> None
        | "AgentOwnerRoot", Some "Child" -> Some "AgentOwnerChildWork"
        | "AgentOwnerRoot", Some "Attached" -> Some "AgentOwnerAttachedWork"
        | "AgentOwnerRoot", Some "InternalLeaf" -> Some "AgentOwnerInternalLeaf"
        | "AgentOwnerRoot", None -> None
        | _ -> None

    /// The closure kind and the agent-owner life of a root acceptance.
    ///
    /// The life is the one the producer proved; it is never invented here.
    /// An agent-owner root whose life the producer could not prove yields no
    /// kind at all, and so admits no closure — the alternative would be a
    /// closure kind guessed from the root's shape, which is exactly the
    /// inference WHAT-018 forbids.
    ///
    /// Returns None when the root kind is unknown, or when an agent-owner
    /// root has no proven life.
    let deriveClosureKind
        (rootKindLabel: string)
        (isManager: bool)
        (agentOwnerLife: string option)
        : (string * string option) option =
        match rootKindLabel with
        | "HumanRoot" ->
            expectedClosureKind rootKindLabel isManager None
            |> Option.map (fun kind -> (kind, None))
        | "AgentOwnerRoot" ->
            expectedClosureKind rootKindLabel isManager agentOwnerLife
            |> Option.map (fun kind -> (kind, agentOwnerLife))
        | _ -> None

    /// The closure kind and agent-owner life to record for an acceptance.
    ///
    /// An agent-owner root whose producer could not prove a life records the
    /// documented coarse life. It must never fall back to a HumanRoot kind:
    /// that records a closure the root can never close, and the journal
    /// rejects the acceptance as a semantic cut.
    let closureFor
        (rootKindLabel: string)
        (isManager: bool)
        (agentOwnerLife: string option)
        : (string * string option) option =
        match deriveClosureKind rootKindLabel isManager agentOwnerLife with
        | Some derived -> Some derived
        | None when rootKindLabel = "AgentOwnerRoot" ->
            expectedClosureKind rootKindLabel isManager (Some coarseAgentOwnerLife)
            |> Option.map (fun kind -> kind, Some coarseAgentOwnerLife)
        | None -> None

    /// The closure kind and agent-owner life of an accepted root, keyed on the
    /// root authority DU itself and total over it: a HumanRoot's kind follows from
    /// its recorded role; an agent-owner root uses its proven life when it is one
    /// of the known labels, and the documented coarse life otherwise. There is no
    /// input for which this fails, so callers never need a "closure kind could
    /// not be proven" rejection path. The label-based `closureFor` remains the
    /// owner of the durable-data boundary, where a foreign payload may fail to
    /// derive a kind.
    let closureKindForRoot (kind: PromptRootAuthorityKind) (isManager: bool) (provenLife: string option) : string * string option =
        match kind with
        | PromptRootAuthorityKind.HumanRoot ->
            (if isManager then "HumanRootManagerLife" else "HumanRootManagedRun"), None
        | PromptRootAuthorityKind.AgentOwnerRoot ->
            let life =
                match provenLife with
                | Some life when List.contains life agentOwnerLifeLabels -> life
                | _ -> coarseAgentOwnerLife
            let kind =
                match life with
                | "Child" -> "AgentOwnerChildWork"
                | "Attached" -> "AgentOwnerAttachedWork"
            // the label filter above leaves "InternalLeaf" as the only remaining label
                | _ -> "AgentOwnerInternalLeaf"
            kind, Some life

    /// The closure kind to record: the recorded one only while it belongs to
    /// this root kind, otherwise the kind the acceptance derives.
    ///
    /// A recorded kind from another root kind can never be closed by this
    /// root. Writing it makes the journal reject the acceptance as a semantic
    /// cut (the whole process dies), so the boundary normalizes it to the
    /// derived kind instead of forwarding a record no closure can honour.
    let recordedClosureKind
        (rootKindLabel: string)
        (isManager: bool)
        (agentOwnerLife: string option)
        (recorded: string option)
        : string =
        let derived =
            closureFor rootKindLabel isManager agentOwnerLife
            |> Option.map fst
            |> Option.defaultValue "HumanRootManagedRun"

        match recorded with
        | Some kind when not (System.String.IsNullOrWhiteSpace kind) ->
            match
                (try
                    Some(rootKindOfKind kind)
                 with _ ->
                     None)
            with
            | Some rootKind when rootKind = rootKindLabel -> kind
            | _ -> derived
        | _ -> derived
