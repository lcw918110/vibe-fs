namespace Wanxiangshu.Foundation

module Identity =
    type RuntimeId = private RuntimeId of string
    type SessionId = private SessionId of string
    type ChildId = private ChildId of string
    type ProcessId = private ProcessId of string
    type EventId = private EventId of string
    type LocalSeq = private LocalSeq of int64
    type JournalRevision = private JournalRevision of int64
    type LocalEpoch = int64
    type ObservedAt = System.DateTimeOffset
    type LogicalRunId = private LogicalRunId of string
    type AuthorityRootUserMessageId = private AuthorityRootUserMessageId of string
    type PhysicalUserMessageId = private PhysicalUserMessageId of string
    type PromptKey = private PromptKey of string
    type RepairRequestId = private RepairRequestId of string
    type TransportReceipt = private TransportReceipt of string
    type ProviderRunIdentity = private ProviderRunIdentity of string

    type ToolCallId = private ToolCallId of string
    type HostToolPartId = private HostToolPartId of string
    type HostMessagePartId = private HostMessagePartId of string
    type TranscriptMessageAddress = private TranscriptMessageAddress of string

    [<RequireQualifiedAccess>]
    type TranscriptGap =
        | Start
        | Before of TranscriptMessageAddress
        | After of TranscriptMessageAddress

    type SystemPromptId = private SystemPromptId of string
    type BlobRef = private BlobRef of string
    type BlobDigest = private BlobDigest of string
    type FrameEpochId = private FrameEpochId of int64
    type PrefixEpochId = private PrefixEpochId of int64
    type AgentHandleId = private AgentHandleId of string
    type PtyHandleId = private PtyHandleId of string
    type ManagerJobId = private ManagerJobId of string

    [<RequireQualifiedAccess>]
    type HandleId =
        | Agent of AgentHandleId
        | Pty of PtyHandleId
        | ManagerJob of ManagerJobId

    type WorktreeIdentity = private WorktreeIdentity of string
    type WorktreePath = private WorktreePath of string
    type TargetRef = private TargetRef of string
    type CommitHash = private CommitHash of string

    module RuntimeId =
        val create: value: string -> RuntimeId
        val value: id: RuntimeId -> string

    module SessionId =
        val create: value: string -> SessionId
        val value: id: SessionId -> string

    module ChildId =
        val create: value: string -> ChildId
        val value: id: ChildId -> string

    module ProcessId =
        val create: value: string -> ProcessId
        val value: id: ProcessId -> string

    module EventId =
        val create: value: string -> EventId
        val value: id: EventId -> string

    module LocalSeq =
        val create: int64 -> LocalSeq
        val value: sequence: LocalSeq -> int64

    module JournalRevision =
        val create: int64 -> JournalRevision
        val value: revision: JournalRevision -> int64
        val initial: JournalRevision
        val next: revision: JournalRevision -> JournalRevision
        val isAfter: left: JournalRevision -> right: JournalRevision -> bool

    module LogicalRunId =
        val create: value: string -> LogicalRunId
        val value: id: LogicalRunId -> string

    module AuthorityRootUserMessageId =
        val create: value: string -> AuthorityRootUserMessageId
        val value: id: AuthorityRootUserMessageId -> string

    module PhysicalUserMessageId =
        val create: value: string -> PhysicalUserMessageId
        val value: id: PhysicalUserMessageId -> string
        val isNonBlank: id: PhysicalUserMessageId -> bool
        val promoteToAuthorityRoot: id: PhysicalUserMessageId -> AuthorityRootUserMessageId

    module PromptKey =
        val create: value: string -> PromptKey
        val value: key: PromptKey -> string

    module RepairRequestId =
        val create: value: string -> RepairRequestId
        val value: id: RepairRequestId -> string

    module TransportReceipt =
        val create: value: string -> TransportReceipt
        val value: receipt: TransportReceipt -> string
        val isAdmissionShaped: receipt: TransportReceipt -> bool

    module ProviderRunIdentity =
        val create: value: string -> ProviderRunIdentity
        val value: id: ProviderRunIdentity -> string



    module ToolCallId =
        val create: value: string -> ToolCallId
        val value: id: ToolCallId -> string

    module HostToolPartId =
        val create: value: string -> HostToolPartId
        val value: id: HostToolPartId -> string

    module HostMessagePartId =
        val create: value: string -> HostMessagePartId
        val value: id: HostMessagePartId -> string

    module TranscriptMessageAddress =
        val create: value: string -> TranscriptMessageAddress
        val value: address: TranscriptMessageAddress -> string

    module SystemPromptId =
        val create: value: string -> SystemPromptId
        val value: id: SystemPromptId -> string

    module BlobRef =
        val create: value: string -> BlobRef
        val value: blobRef: BlobRef -> string

    module BlobDigest =
        val create: value: string -> BlobDigest
        val value: digest: BlobDigest -> string

    module FrameEpochId =
        val create: value: int64 -> FrameEpochId
        val value: id: FrameEpochId -> int64
        val initial: FrameEpochId
        val next: id: FrameEpochId -> FrameEpochId

    module PrefixEpochId =
        val create: value: int64 -> PrefixEpochId
        val value: id: PrefixEpochId -> int64
        val initial: PrefixEpochId
        val next: id: PrefixEpochId -> PrefixEpochId

    module AgentHandleId =
        val create: value: string -> AgentHandleId
        val value: id: AgentHandleId -> string

    module PtyHandleId =
        val create: value: string -> PtyHandleId
        val value: id: PtyHandleId -> string

    module ManagerJobId =
        val create: value: string -> ManagerJobId
        val value: id: ManagerJobId -> string

    module HandleId =
        val describe: handle: HandleId -> string
        val tryAgent: handle: HandleId -> AgentHandleId option

    module WorktreeIdentity =
        val create: value: string -> WorktreeIdentity
        val value: identity: WorktreeIdentity -> string

    module WorktreePath =
        val create: value: string -> WorktreePath
        val value: path: WorktreePath -> string

    module TargetRef =
        val create: value: string -> TargetRef
        val value: targetRef: TargetRef -> string

    module CommitHash =
        val create: value: string -> CommitHash
        val value: hash: CommitHash -> string
