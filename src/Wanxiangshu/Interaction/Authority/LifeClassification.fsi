namespace Wanxiangshu.Interaction.Authority

/// The closed set of closure kinds and the agent-owner life labels, mirroring
/// the canonical law in `wanxiangli/src/authority/ClosureLaw.fs`.
[<RequireQualifiedAccess>]
module AuthorityLifeClassification =
    /// The closed set of expected closure kinds.
    val closureKindLabels: string list

    /// The closed set of agent-owner life labels.
    val agentOwnerLifeLabels: string list

    /// The root kind label of a closure kind: "HumanRoot" | "AgentOwnerRoot".
    val rootKindOfKind: closureKind: string -> string

    /// The closure witness kind a closure of this kind must carry.
    val witnessKindOf: closureKind: string -> string option

    /// The coarse agent-owner life used when the host cannot observe the life
    /// of an agent-owner root at acceptance time.
    val coarseAgentOwnerLife: string

    /// The closure kind this root acceptance expects, mirroring
    /// `ClosureLaw.expectedKind` exactly. None when the inputs cannot determine
    /// a unique kind.
    val expectedClosureKind: rootKindLabel: string -> isManager: bool -> agentOwnerLife: string option -> string option

    /// The closure kind and the agent-owner life of a root acceptance.
    ///
    /// The life is the one the producer proved; it is never invented here. An
    /// agent-owner root whose life the producer could not prove yields no
    /// kind at all, and so admits no closure.
    val deriveClosureKind:
        rootKindLabel: string -> isManager: bool -> agentOwnerLife: string option -> (string * string option) option

    /// The closure kind and agent-owner life to record for an acceptance.
    ///
    /// An agent-owner root whose producer could not prove a life records the
    /// documented coarse life. It must never fall back to a HumanRoot kind.
    val closureFor:
        rootKindLabel: string -> isManager: bool -> agentOwnerLife: string option -> (string * string option) option

    /// The closure kind and agent-owner life of an accepted root, total over
    /// the root authority DU: a HumanRoot follows its recorded role, an
    /// agent-owner uses its proven life or the coarse fallback. The in-process
    /// counterpart of `closureFor`, which stays the owner of the durable-data
    /// boundary.
    val closureKindForRoot: kind: PromptRootAuthorityKind -> isManager: bool -> provenLife: string option -> string * string option

    /// The closure kind to record: the recorded one only while it belongs to
    /// this root kind, otherwise the kind the acceptance derives.
    val recordedClosureKind:
        rootKindLabel: string -> isManager: bool -> agentOwnerLife: string option -> recorded: string option -> string
