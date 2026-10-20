namespace Wanxiangshu.Process

open System.Threading.Tasks
open Wanxiangshu.Execution.Delegation.Fork
open Wanxiangshu.Execution.Session

module ProcessSurface =
    val killAckGraceMs: int

    val createVirtualTimer: unit -> obj
    val timerDelay: timer: obj -> milliseconds: int -> obj
    val timerAwait: handle: obj -> Task<unit>
    val timerCancel: handle: obj -> unit
    val timerAdvance: timer: obj -> milliseconds: int -> unit
    val timerNowMs: timer: obj -> int
    val timerDispose: timer: obj -> unit

    val createNodeTimer: unit -> obj
    val nodeTimerDispose: timer: obj -> unit

    val createVirtualClock: unit -> obj
    val clockNowIso: clock: obj -> string
    val clockNowMs: clock: obj -> int64
    val clockAdvanceMs: clock: obj -> milliseconds: int -> unit
    val clockSet: clock: obj -> iso: string -> unit
    val createNodeClock: unit -> obj

    val effectiveDeadlineSeconds: runtimeSeconds: float -> hardLimitSeconds: float -> float
    val outputThreshold: bytes: float -> float
    val validateEstimate: runtimeSeconds: float -> outputBytes: float -> obj
    val defaultHardLimitSeconds: float
    val renderDeadlineExpired: unit -> string
    val renderElapsed: language: string -> elapsedMilliseconds: float -> string
    val composeWithElapsed: tip: obj -> elapsed: obj -> estimate: obj -> guideline: string -> string

    val sessionStartBind: nowIso: string -> current: obj -> obj
    val sessionStartAt: state: obj -> string
    val createSessionStartLedger: unit -> obj
    val appendSessionStart: ledger: obj -> sessionId: string -> startedAt: string -> unit
    val readSessionStart: ledger: obj -> sessionId: string -> obj

    val command: fileName: string -> arguments: obj -> workingDirectory: obj -> stdin: obj -> obj
    val commandView: value: obj -> obj
    val estimate: runtimeSeconds: float -> outputBytes: float -> memory: string -> obj
    val estimateView: value: obj -> obj
    val context: workingDirectory: obj -> hardLimitMs: float -> obj
    val contextView: value: obj -> obj

    val createCancellationToken: cancelled: obj -> obj
    val cancel: token: obj -> unit
    val cancelToken: token: obj -> obj
    val tokenView: token: obj -> obj
    val registerCancellation: token: obj -> callback: obj -> obj
    val disposeCancellationRegistration: _registration: obj -> unit

    val outcomeView: outcome: obj -> obj
    val resultView: result: obj -> obj

    val runWithLauncher: launcher: obj -> command: obj -> estimate: obj -> context: obj -> token: obj -> Task<obj>

    val runWithHost: command: obj -> estimate: obj -> context: obj -> token: obj -> Task<obj>
    val run: command: obj -> estimate: obj -> context: obj -> token: obj -> Task<obj>

    val childCreate: onKill: obj -> obj
    val childExit: child: obj -> code: int -> unit
    val childOnExit: child: obj -> callback: obj -> unit
    val childView: child: obj -> obj
    val waitOutcomeView: outcome: obj -> obj
    val waitForExit: child: obj -> deadline: obj -> token: obj -> Task<obj>

    val outputCreate: estimate: obj -> obj
    val outputAddStdout: collector: obj -> bytes: obj -> unit
    val outputAddStderr: collector: obj -> bytes: obj -> unit
    val outputBuildResult: collector: obj -> exitCode: int -> obj
    val outputView: collector: obj -> obj

    val spoolChunkCount: bytes: float -> int
    val spoolChunkBytes: chunkSize: int -> bytes: obj -> obj
    val spoolStart: unit -> obj
    val spoolAppend: spool: obj -> bytes: obj -> unit
    val spoolPath: spool: obj -> string
    val spoolBytesWritten: spool: obj -> float
    val spoolRead: spool: obj -> Task<obj array>
    val spoolDelete: path: string -> unit
    val spoolReadPath: path: string -> Task<obj array>
    val spoolBytesToTempFile: bytes: obj -> obj

    val bytes: text: string -> obj
    val newId: unit -> obj
    val ptyIdView: id: obj -> string
    val registerParentAbort: parentId: string -> callback: obj -> int
    val unregisterParentAbort: parentId: string -> token: int -> unit
    val abortParent: parentId: string -> unit

    val signalParse: name: string -> obj
    val commandSpawn: command: string -> cwd: string -> obj
    val commandWrite: value: obj -> obj
    val commandRead: unit -> obj
    val commandSignal: name: string -> obj
    val commandResize: width: int -> height: int -> obj
    val ptyCommandView: value: obj -> obj

    val completionView: item: obj -> obj
    val ptyExited: id: string -> outcome: string -> obj
    val ptyFailed: id: string -> message: string -> obj
    val ptyAborted: id: string -> message: string -> obj

    val ptySignalParse: name: string -> obj
    val ptySignalView: name: string -> obj
    val ptyCommandSpawn: command: string -> cwd: string -> obj
    val ptyCommandWrite: value: obj -> obj
    val ptyCommandRead: unit -> obj
    val ptyCommandSignal: name: string -> obj
    val ptyCommandResize: width: int -> height: int -> obj
    val ptyId: value: string -> obj
    val ptyHandleView: handle: PtyHandle -> obj
    val ptyReadView: read: PtyRead -> obj

    val createPtyPort: options: obj -> obj
    val backendCreatePort: unit -> obj
    val portAddMailboxSender: port: obj -> sender: obj -> unit
    val portFork: port: obj -> command: string -> agentName: string -> ptyId: obj -> cwd: obj -> obj
    val portExists: port: obj -> id: obj -> bool
    val portKnown: port: obj -> id: obj -> bool
    val portSend: port: obj -> id: obj -> command: obj -> Task<obj>
    val portRead: port: obj -> id: obj -> Task<obj>
    val portReadResult: port: obj -> id: obj -> output: string -> closed: bool -> unit
    val portFailRead: port: obj -> id: obj -> reason: string -> unit
    val portRegisterExitTask: port: obj -> id: obj -> taskValue: obj -> unit
    val ptyRaceExit: exitTask: obj -> milliseconds: int -> Task<bool>
    val portComplete: port: obj -> id: obj -> outcome: obj -> unit
    val portCompleteAborted: port: obj -> id: obj -> message: obj -> unit
    val portClose: port: obj -> id: obj -> unit
    val portCloseAll: port: obj -> graceMs: obj -> Task<unit>
    val agentView: agent: AgentRecord -> obj
    val portList: port: obj -> obj

    val sessionCreate: id: string -> backend: obj -> obj
    val sessionView: session: obj -> obj
    val sessionSetClosed: session: obj -> closed: bool -> unit
    val sessionSetBackend: session: obj -> backend: obj -> unit
    val sessionAppendOutput: session: obj -> text: string -> unit
    val sessionPushPending: session: obj -> command: obj -> unit
    val sessionPushPendingTask: session: obj -> command: obj -> Task<obj>
    val sessionExitPending: session: obj -> bool
    val sessionResolveExit: session: obj -> unit
    val sessionPendingView: session: obj -> obj array

    val ptySessionCreate: id: string -> backend: obj -> obj
    val ptySessionView: session: obj -> obj
    val ptySessionSetClosed: session: obj -> closed: bool -> unit
    val ptySessionSetBackend: session: obj -> backend: obj -> unit
    val ptySessionPushPending: session: obj -> command: obj -> unit
    val ptySessionExitPending: session: obj -> bool
    val ptySessionResolveExit: session: obj -> unit

    val supervisorCreate: unit -> obj
    val supervisorAdd: supervisor: obj -> id: obj -> session: obj -> unit
    val supervisorTryGet: supervisor: obj -> id: obj -> obj
    val supervisorGet: supervisor: obj -> id: obj -> obj
    val supervisorRemove: supervisor: obj -> id: obj -> unit
    val supervisorList: supervisor: obj -> string array
    val supervisorSignalName: name: string -> obj
    val supervisorEnsureSpawn: supervisor: obj -> Task<unit>
    val supervisorSpawn: supervisor: obj -> command: string -> cwd: string -> Task<obj>
    val supervisorFailPending: pending: obj -> reason: string -> unit
    val supervisorTakePending: supervisor: obj -> id: obj -> obj
    val supervisorDropPending: supervisor: obj -> id: obj -> obj
    val supervisorApplyLive: supervisor: obj -> port: obj -> id: obj -> command: obj -> Task<obj>
    val supervisorAttach: supervisor: obj -> port: obj -> id: obj -> term: obj -> unit
    val supervisorCancelAll: supervisor: obj -> unit
    val supervisorSetSpawn: supervisor: obj -> spawn: obj -> unit
    val supervisorPendingEntries: pending: obj -> obj array

    val pendingCommands: pending: obj -> obj array
    val pendingEntryView: entry: obj -> obj
    val pendingResolve: pending: obj -> index: int -> result: obj -> unit
    val readPlanView: plan: ReadPlan -> obj

    val renderPtyCompletion: label: string -> _id: string -> outcome: string -> exitCode: int -> string

    val runWithHostLauncher: host: obj -> command: obj -> estimate: obj -> context: obj -> token: obj -> Task<obj>
