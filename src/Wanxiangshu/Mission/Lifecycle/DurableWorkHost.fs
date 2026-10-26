namespace Wanxiangshu.Mission.Lifecycle

open System
open Fable.Core
open Fable.Core.JsInterop

/// Host side of wanxiangli durable work. Opens and closes the sqlite journal
/// in this module. Callers send a public command and receive a host observation.
/// They do not hold the connection.
module DurableWorkHost =

    [<Import("createRequire", "node:module")>]
    let private createRequire (_url: string) : obj = jsNative

    [<Emit("import.meta.url")>]
    let private moduleUrl: string = jsNative

    [<Emit("typeof globalThis.Bun !== 'undefined'")>]
    let private runningOnBun () : bool = jsNative

    [<Emit("$0($1)")>]
    let private requireModule (_require: obj) (_name: string) : obj = jsNative

    [<Import("SqliteJournal", "wanxiangli/eventstore")>]
    let private SqliteJournal: obj = jsNative

    [<Import("DurableWork", "wanxiangli/durable-work")>]
    let private DurableWork: obj = jsNative

    [<Import("Work", "wanxiangli/work")>]
    let private Work: obj = jsNative

    [<Import("Ownership", "wanxiangli/ownership")>]
    let private Ownership: obj = jsNative

    [<Import("mkdirSync", "node:fs")>]
    let private mkdirSync (path: string) (options: obj) : unit = jsNative

    [<Import("resolve", "node:path")>]
    let private resolvePath (path: string) : string = jsNative

    [<Import("existsSync", "node:fs")>]
    let private pathExists (path: string) : bool = jsNative

    [<Import("realpathSync", "node:fs")>]
    let private physicalPath (path: string) : string = jsNative

    [<Import("dirname", "node:path")>]
    let private directoryOf (path: string) : string = jsNative

    let private rejectGitPath (path: string) =
        let isGit (value: string) =
            value.Split([| '/'; '\\' |], StringSplitOptions.RemoveEmptyEntries)
            |> Array.exists (fun part -> part.Equals(".git", StringComparison.OrdinalIgnoreCase))

        if isGit path then
            invalidArg "stateRoot" "Lifecycle persistence cannot read or write inside .git"

        let assertNotGitLink (candidate: string) =
            if physicalPath candidate |> isGit then
                invalidArg "stateRoot" "Lifecycle persistence cannot follow a link into .git"

        // 文件尚未存在时也核对已有父目录，防止通过链接回到旧 .git。
        let rec checkExisting candidate =
            if pathExists candidate then
                assertNotGitLink candidate
            elif directoryOf candidate <> candidate then
                checkExisting (directoryOf candidate)
            else
                ()

        checkExisting path

    let private ensureDir (path: string) =
        mkdirSync path (createObj [ "recursive" ==> true ])

    let private openDatabase (path: string) : obj =
        let require = createRequire moduleUrl

        if runningOnBun () then
            let sqlite = requireModule require "bun:sqlite"
            createNew sqlite?Database path
        else
            let sqlite = requireModule require "node:sqlite"
            createNew sqlite?DatabaseSync path

    [<Emit("$0 != null && (($0.code === 'SQLITE_BUSY' || $0.code === 'SQLITE_BUSY_SNAPSHOT') || (typeof $0.message === 'string' && $0.message.indexOf('database is locked') >= 0))")>]
    let private isDatabaseBusy (_error: exn) : bool = jsNative

    let private waitForDatabase () : unit =
        emitJsExpr () "Atomics.wait(new Int32Array(new SharedArrayBuffer(4)), 0, 0, 20)"

    let private retryBusy (action: unit -> 'a) : 'a =
        let rec attempt remaining =
            try
                action ()
            with error when isDatabaseBusy error && remaining > 0 ->
                waitForDatabase ()
                attempt (remaining - 1)

        attempt 25

    [<Emit("$0 != null && typeof $0 === 'object'")>]
    let private isRecord (_value: obj) : bool = jsNative

    [<Emit("typeof $0 === 'string'")>]
    let private isString (_value: obj) : bool = jsNative

    [<Emit("Array.isArray($0) ? $0 : []")>]
    let private asArray (_value: obj) : obj array = jsNative

    [<Emit("$0 != null && (typeof $0 === 'object' || typeof $0 === 'function') && typeof $0.then === 'function'")>]
    let private isThenable (_value: obj) : bool = jsNative

    [<Emit("$0.then($1)")>]
    let private continueWith (_promise: obj) (_step: obj -> obj) : obj = jsNative

    [<Emit("$0.then($1, $2)")>]
    let private continueEither (_promise: obj) (_ok: obj -> obj) (_err: obj -> obj) : obj = jsNative

    let private settle (produced: obj) (step: obj -> obj) : obj =
        if isThenable produced then continueWith produced step else step produced

    /// Sibling of `.Herdchestra/wanxiangshu/events/`, not inside it. The library takes a
    /// connection, not a path; this file is the host-owned journal for new work.
    let journalPath (stateRoot: string) : string =
        if String.IsNullOrWhiteSpace stateRoot then
            invalidArg "stateRoot" "A local state root is required"

        rejectGitPath stateRoot
        let path = resolvePath (stateRoot.TrimEnd('/', '\\') + "/wanxiangshu/work.sqlite")
        rejectGitPath path
        path

    type HostReceipt =
        { RequestId: string
          Status: string
          Kind: string
          Reason: string
          Revision: float
          Result: obj }

    let private text (value: obj) : string =
        if isString value then unbox value else ""

    let private nested (value: obj) (name: string) : obj =
        if isRecord value then value?(name) else null

    /// The request id that entered is the request id that leaves. This function
    /// does not mint a replacement.
    let observe (requestId: string) (durableResult: obj) : HostReceipt =
        let status = text (nested durableResult "status")
        let result = nested durableResult "result"
        let kind = if isRecord result then text (result?kind) else ""
        let reasonValue = nested durableResult "reason"
        let revisionValue = nested durableResult "revision"

        let revision =
            if isNull revisionValue then
                0.
            else
                unbox<float> revisionValue

        { RequestId = requestId
          Status = status
          Kind = kind
          Reason = text reasonValue
          Revision = revision
          Result = result }

    let private admittedStatus status =
        status = "Committed" || status = "Replayed" || status = "Observed"

    let admitted (receipt: HostReceipt) : bool =
        admittedStatus receipt.Status && not (String.IsNullOrEmpty receipt.Kind)

    /// Published decision fields. DeliveryRequested carries operationId, digest, and path.
    let resultText (receipt: HostReceipt) (name: string) : string = text (nested receipt.Result name)

    /// First physical start is allowed only for a committed DeliveryRequested.
    /// Replayed, Observed, Unknown, NotWritten, Conflict, and any other tag are not a start.
    let mayStart (durableResult: obj) : bool =
        let seen = observe "" durableResult
        seen.Status = "Committed" && seen.Kind = "DeliveryRequested"

    let command (kind: string) (fields: (string * obj) list) : obj =
        createObj (("type", box kind) :: fields)

    let planSequence () : obj = box [| "S1"; "S2"; "S3" |]

    let executionSequence () : obj = box [| "S4" |]

    let private parentOf (path: string) : string =
        let slash = path.LastIndexOf('/')
        if slash <= 0 then path else path.Substring(0, slash)

    let private closeConnection (connection: obj) : unit = connection?close ()

    let private closeJournal (journal: obj option) (connection: obj) : unit =
        match journal with
        | Some opened -> opened?close ()
        | None -> closeConnection connection

    /// 一个操作持有一个连接，构造失败时也关闭连接。偶发忙碌时短等后重试。
    let private useConnection (openConnection: unit -> obj) (body: obj -> 'a) : 'a =
        retryBusy (fun () ->
            let connection = openConnection ()
            // DSL-MUTABLE: resource — the journal this call opened, closed exactly once in finally
            let mutable journal: obj option = None

            try
                let opened: obj = createNew SqliteJournal connection
                journal <- Some opened
                body opened
            finally
                closeJournal journal connection)

    let private useJournalWith (openConnection: string -> obj) (gitCommonDir: string) (body: obj -> 'a) : 'a =
        let path = journalPath gitCommonDir
        useConnection (fun () -> openConnection path) body

    let private useJournal (gitCommonDir: string) (body: obj -> 'a) : 'a =
        let path = journalPath gitCommonDir

        if pathExists path then
            useJournalWith openDatabase gitCommonDir body
        else
            // 库尚未建立时没有可观察的工作。空内存库会被读成「未交付」，
            // 从而在交付之后继续催促。缺库就是没有工作，由调用方按 None 处理。
            failwith "lifecycle journal does not exist"

    let private useReadableJournal (gitCommonDir: string) (body: obj -> 'a) : 'a option =
        try
            Some(useJournal gitCommonDir body)
        with error when error.Message = "lifecycle journal does not exist" ->
            None

    let private useWritableJournal (gitCommonDir: string) (body: obj -> 'a) : 'a =
        let path = journalPath gitCommonDir
        ensureDir (parentOf path)
        useJournalWith openDatabase gitCommonDir body

    let useWork (gitCommonDir: string) (workId: string) (body: obj -> 'a) : 'a =
        useWritableJournal gitCommonDir (fun journal -> body (createNew DurableWork (workId, journal)))

    let private appendSeed (journal: obj) (workId: string) (revision: float) (record: string) : float =
        let frame: obj = emitJsExpr record "JSON.parse($0)"
        let requestId = text (nested frame "requestId")
        let acknowledgement: obj = journal?append (workId, revision, requestId, record)
        let status = text (nested acknowledgement "status")

        if status <> "Committed" && status <> "Replayed" then
            failwith (status + ": " + text (nested acknowledgement "reason"))

        unbox<float> (nested acknowledgement "revision")

    /// Test/probe host for canonical records. It uses the same runtime-specific
    /// SQLite selection as the durable path and does not expose the connection.
    let useEphemeralWork (workId: string) (records: string array) (body: obj -> 'a) : 'a =
        let connection = openDatabase ":memory:"
        // DSL-MUTABLE: resource — the probe's one journal handle, closed exactly once in finally
        let mutable journal: obj option = None

        try
            let opened: obj = createNew SqliteJournal connection
            journal <- Some opened
            records |> Array.fold (appendSeed opened workId) 0. |> ignore
            body (createNew DurableWork (workId, opened))
        finally
            closeJournal journal connection

    let execute (gitCommonDir: string) (workId: string) (requestId: string) (command: obj) : HostReceipt =
        useWork gitCommonDir workId (fun work ->
            let durable = work?execute (requestId, command)
            observe requestId durable)

    /// A review with no live session reports the effect unknown. It does not
    /// pretend the user was parked, the prompt was sent, or a manager was opened.
    let private honestEffects () : obj =
        let ask (_effect: obj) = "Unknown"
        let dispatch (_effect: obj) = "Unknown"
        let stop (_effect: obj) = "Unknown"

        let createSession (_effect: obj) =
            createObj
                [ "status", box "NotStarted"
                  "reason", box "no manager session is bound to this review" ]

        let sendDraft (_effect: obj) = "Unknown"

        createObj
            [ "ask", box ask
              "dispatch", box dispatch
              "stop", box stop
              "createSession", box createSession
              "sendDraft", box sendDraft ]

    let private succeeded (result: obj) = isRecord result && unbox<bool> result?ok

    /// The review coordinator calls these ports only after the domain commit.
    /// park, send, draft and stop receive one request object and return
    /// `{ ok: true }` or `{ ok: false, reason }`, or a promise of that object.
    /// open returns `{ ok: true, sessionId }` or `{ ok: false, reason }`.
    let reviewEffects (ports: obj) : obj =
        let ask (effect: obj) =
            settle (ports?park (createObj [ "text", effect?text ])) (fun result ->
                if succeeded result then "Parked" else "Unknown")

        let dispatch (effect: obj) =
            settle (ports?send (createObj [ "text", effect?text ])) (fun result ->
                if succeeded result then "Sent" else "Unknown")

        let createSession (effect: obj) =
            settle (ports?``open`` (createObj [ "digest", effect?digest ])) (fun result ->
                if succeeded result && isString result?sessionId && not (String.IsNullOrEmpty(unbox<string> result?sessionId)) then
                    createObj
                        [ "status", box "SessionCreated"
                          "execSessionId", result?sessionId ]
                else
                    let reason =
                        if isString result?reason && not (String.IsNullOrEmpty(unbox<string> result?reason)) then
                            unbox<string> result?reason
                        else
                            "manager session was not created"

                    createObj [ "status", box "NotStarted"; "reason", box reason ])

        let sendDraft (effect: obj) =
            settle
                (ports?draft (
                    createObj
                        [ "sessionId", effect?execSessionId
                          "digest", effect?digest ]
                ))
                (fun result -> if succeeded result then "Admitted" else "Unknown")

        let stop (effect: obj) =
            settle (ports?stop (createObj [ "incumbencyId", effect?incumbencyId ])) (fun result ->
                if succeeded result then "Requested" else "Unknown")

        createObj
            [ "ask", box ask
              "dispatch", box dispatch
              "stop", box stop
              "createSession", box createSession
              "sendDraft", box sendDraft ]

    /// The journal stays open until a thenable effect chain has confirmed.
    /// Closing in a synchronous finally would drop the confirmation append.
    let private executeWithEffects
        (gitCommonDir: string)
        (workId: string)
        (requestId: string)
        (command: obj)
        (effects: obj)
        : obj =
        let path = journalPath gitCommonDir
        ensureDir (parentOf path)

        let run () =
            let connection = openDatabase path
            let opened: obj = createNew SqliteJournal connection

            let close () =
                closeJournal (Some opened) connection

            try
                let durableWork: obj = createNew DurableWork (workId, opened, effects)
                let produced: obj = durableWork?execute (requestId, command)

                if isThenable produced then
                    continueEither
                        produced
                        (fun value ->
                            close ()
                            value)
                        (fun error ->
                            close ()
                            failwith (if isNull error then "review effect failed" else string error))
                else
                    close ()
                    produced
            with error ->
                close ()
                raise error

        retryBusy run

    /// The only lifecycle command the host sends. Office, call identity, and the
    /// obligation updates are the caller's; Work decides relay, advance, and close.
    let submitReview
        (gitCommonDir: string)
        (workId: string)
        (requestId: string)
        (office: string)
        (providerRunId: string)
        (toolCallId: string)
        (updates: obj)
        (blockers: obj)
        (digest: string)
        (effects: obj)
        : obj =
        let fields =
            [ "office", box office
              "providerRunId", box providerRunId
              "toolCallId", box toolCallId
              "updates", if isNull updates then box ([||]: obj array) else updates ]

        let withBlockers =
            if isNull blockers then
                fields
            else
                ("blockers", blockers) :: fields

        let withDigest =
            if String.IsNullOrEmpty digest then
                withBlockers
            else
                ("digest", box digest) :: withBlockers

        let ports = if isNull effects then honestEffects () else effects

        let present (durable: obj) =
            let receipt = observe requestId durable

            createObj
                [ "requestId", box receipt.RequestId
                  "status", box receipt.Status
                  "kind", box receipt.Kind
                  "reason", box receipt.Reason
                  "revision", box receipt.Revision
                  "milestone", box (resultText receipt "milestone")
                  "wait", box (resultText receipt "wait")
                  "incumbencyId", box (resultText receipt "incumbencyId")
                  "blockers", nested receipt.Result "blockers" ]

        let raw = executeWithEffects gitCommonDir workId requestId (command "Review" withDigest) ports
        if isThenable raw then continueWith raw present else present raw

    let inspect (gitCommonDir: string) (workId: string) : obj =
        useWork gitCommonDir workId (fun work -> work?inspect ())

    let private textOf (value: obj) : string =
        if isNull value then "" else string value

    /// A provider run / tool call pair as recorded by a committed lifecycle fact.
    type HostRunFence =
        { ProviderRunId: string
          ToolCallId: string }

    /// One committed IncumbencyRetired fact. ProjectionCut is true when the
    /// retirement came from the execution sequence and carries no cursor.
    /// Expected is the authority the retiring tenure expected to advance from.
    type HostRetirement =
        { RetirementId: string
          IncumbencyId: string
          Outcome: string
          Expected: string
          Fence: HostRunFence
          ProjectionCut: bool }

    /// One committed AdvanceCommitted fact: which tenure moved authority from
    /// Expected to Next.
    type HostAdvance =
        { IncumbencyId: string
          Expected: string
          Next: string }

    /// One keyed obligation entry of the active tenure's ledger.
    type HostObligation = { Key: string; Value: string }

    /// Fields the old readers already used, plus the retirement list,
    /// confirmation identity, projection cut, and advance history the
    /// recovery points need.
    type InspectedWork =
        {
            Opened: bool
            Sequence: string list
            Stage: string
            /// The work snapshot's wait: AwaitReview, AwaitOperatorAnswer,
            /// AwaitEmptyReview, AwaitOperatorMessage, or ConfirmEffect.
            Wait: string
            ActiveId: string option
            OpeningStage: string option
            Reviewed: bool
            ReviewOutcome: string option
            /// The active tenure's keyed obligation ledger. A non-empty ledger
            /// blocks deliver; each review updates it item by item.
            Obligations: obj array
            OpeningCursor: float option
            PredecessorRange: (float * float) option
            LastRetirementCursor: float option
            DeliveryAuthorized: bool
            Delivered: bool
            PendingDelivery: bool
            PendingOperationId: string option
            PendingExecSessionId: string option
            PendingDigest: string option
            PendingPath: string option
            RoundCount: int
            RetiredRuns: string list
            DevOpsId: string option
            DevOpsTarget: string option
            Authority: string
            BlockerDigest: string option
            Root: string option
            Retirements: HostRetirement list
            ProjectionCut: HostRunFence option
            Advances: HostAdvance list
            BarrierToolCallId: string option
            SuccessorTenureId: string option
        }

    let private optionalText (value: string) : string option = if value = "" then None else Some value

    let private activeIdOf (active: obj) : string option =
        if isNull active then None else Some(textOf active?id)

    let private obligationsOf (active: obj) : obj array =
        if isNull active || isNull active?obligations then
            [||]
        else
            unbox<obj array> active?obligations
            |> Array.map (fun entry ->
                box
                    {| key = textOf entry?key
                       value = textOf entry?value |})

    let private retirementsOf (snapshot: obj) : HostRetirement list =
        asArray snapshot?rounds
        |> Array.choose (fun round ->
            let retirementId = textOf round?retirementId

            if retirementId = "" then
                None
            else
                Some
                    { RetirementId = retirementId
                      IncumbencyId = textOf round?incumbencyId
                      Outcome = textOf round?outcome
                      Expected = textOf round?milestone
                      Fence =
                        { ProviderRunId = ""
                          ToolCallId = "" }
                      ProjectionCut = false })
        |> List.ofArray

    /// A completed work closes its authority run. An open milestone does not.
    let closesAuthority (work: InspectedWork) =
        work.Delivered || work.Stage = "已完成"

    /// The retirement id of a completed work with no active tenure.
    let completedClosureId (work: InspectedWork) =
        if work.ActiveId.IsSome || not (closesAuthority work) then
            None
        else
            work.Retirements
            |> List.tryFind (fun retirement -> retirement.Outcome = "Completed" || retirement.Outcome = "Delivered")
            |> Option.map (fun retirement -> retirement.RetirementId)

    /// Current work snapshot. Milestone and the active tenure are the authority.
    /// The old S-sequence, eligibility flag, and delivery authorization are absent.
    let private projectCurrent (snapshot: obj) : InspectedWork =
        let active: obj = snapshot?active
        let rounds = asArray snapshot?rounds
        let completed = not (isNull snapshot?completed) && unbox<bool> snapshot?completed
        let barrier: obj = snapshot?barrier

        { Opened = true
          Sequence = []
          Stage = textOf snapshot?milestone
          Wait = textOf snapshot?wait
          ActiveId = activeIdOf active
          OpeningStage = None
          Reviewed = false
          ReviewOutcome = None
          Obligations = obligationsOf active
          OpeningCursor = None
          PredecessorRange = None
          LastRetirementCursor = None
          DeliveryAuthorized = false
          Delivered = completed
          PendingDelivery = false
          PendingOperationId = None
          PendingExecSessionId = None
          PendingDigest = None
          PendingPath = None
          RoundCount = rounds.Length
          RetiredRuns = []
          DevOpsId = optionalText (textOf snapshot?devopsId)
          DevOpsTarget = optionalText (textOf snapshot?devopsTarget)
          Authority = ""
          BlockerDigest = None
          Root = None
          Retirements = retirementsOf snapshot
          ProjectionCut = None
          Advances = []
          BarrierToolCallId = if isNull barrier then None else optionalText (textOf barrier?toolCallId)
          SuccessorTenureId = if isNull barrier then None else optionalText (textOf barrier?successorTenureId) }

    let private inspectedFromJournal (journal: obj) (workId: string) : InspectedWork option =
        // 同一份已校验记录只折叠一次，快照与历史属于同一修订。
        let records: string array = journal?read (workId)
        let work: obj = createNew Work (workId, records)
        let snapshot: obj = work?inspect ()

        if String.IsNullOrEmpty(textOf snapshot?office) then
            None
        else
            Some(projectCurrent snapshot)

    let inspected (gitCommonDir: string) (workId: string) : InspectedWork option =
        // An empty id names no work; it never reaches the journal as a query.
        if String.IsNullOrEmpty workId then
            None
        else
            useReadableJournal gitCommonDir (fun journal -> inspectedFromJournal journal workId)
            |> Option.defaultValue None

    let private tryInspectWorkFromJournal (journal: obj) (workId: string) : Result<InspectedWork option, string> =
        try
            Ok(inspectedFromJournal journal workId)
        with error ->
            Error error.Message

    let private safeInspectFromJournal (journal: obj) (workId: string) : Result<InspectedWork option, string> =
        if String.IsNullOrEmpty workId then
            Ok None
        else
            tryInspectWorkFromJournal journal workId

    /// 启动枚举共用一次连接和全库完整性检查，各工作流仍分别校验并报告错误。
    let inspectedMany (gitCommonDir: string) (workIds: string array) =
        if Array.isEmpty workIds then
            [||]
        else
            useReadableJournal gitCommonDir (fun journal ->
                workIds
                |> Array.map (fun workId -> workId, safeInspectFromJournal journal workId))
            |> Option.defaultValue (workIds |> Array.map (fun workId -> workId, Ok None))

    let private expectedSequence (value: obj) =
        asArray value |> Array.toList |> List.map text

    let private matchingOpening (root: string) (sequence: obj) (work: InspectedWork) : Result<InspectedWork, string> =
        let expected = expectedSequence sequence

        if work.Sequence <> expected then
            Error "existing lifecycle work has a different sequence"
        elif work.Root <> Some root then
            Error "existing lifecycle work has a different root"
        else
            Ok work

    /// Idempotent host opening across process races. An in-memory precheck is
    /// not a lock: Conflict or Unknown can still mean another writer committed
    /// the same work. Only a durable reread decides whether opening is settled.
    let ensureOpened
        (gitCommonDir: string)
        (workId: string)
        (requestId: string)
        (root: string)
        (sequence: obj)
        (ownerId: string)
        (openingCursor: float)
        : Result<InspectedWork, string> =
        let readExisting () =
            inspected gitCommonDir workId |> Option.map (matchingOpening root sequence)

        // 第一次 review 开立工作。这里不发 Open，也不把缺席翻译成 review。
        let missing () =
            ignore requestId
            ignore ownerId
            ignore openingCursor
            Error "the first review opens the work"

        match readExisting () with
        | Some result -> result
        | None -> missing ()

    let recover (gitCommonDir: string) (workId: string) : obj =
        useWork gitCommonDir workId (fun work -> work?recover ())

    type OwnershipLease =
        { RegistryId: string
          ResourceId: string
          Token: string }

    type OwnershipBlocker =
        { Lease: OwnershipLease
          ScopeId: string
          Kind: string
          Phase: string
          ExternalId: string option
          IncarnationId: string option
          Outcome: string option
          Reason: string option
          CancellationRequested: bool }

    let ownershipMessage (kind: string) (fields: (string * obj) list) : obj =
        createObj (("type", box kind) :: fields)

    let leaseObject (lease: OwnershipLease) : obj =
        createObj
            [ "registryId", box lease.RegistryId
              "resourceId", box lease.ResourceId
              "token", box lease.Token ]

    let bindingObject (externalId: string) (incarnationId: string) : obj =
        createObj [ "externalId", box externalId; "incarnationId", box incarnationId ]

    let private buildLease (registryId: string) (resourceId: string) (token: string) : OwnershipLease option =
        if registryId = "" || resourceId = "" || token = "" then
            None
        else
            Some
                { RegistryId = registryId
                  ResourceId = resourceId
                  Token = token }

    let parseLease (value: obj) : OwnershipLease option =
        if not (isRecord value) then
            None
        else
            buildLease
                (text (nested value "registryId"))
                (text (nested value "resourceId"))
                (text (nested value "token"))

    let private bindingField (binding: obj) (name: string) : string option =
        if isRecord binding then
            optionalText (text (nested binding name))
        else
            None

    let private parseBlockerItem (item: obj) : OwnershipBlocker option =
        parseLease (nested item "lease")
        |> Option.map (fun lease ->
            let binding = nested item "binding"

            { Lease = lease
              ScopeId = text (nested item "scopeId")
              Kind = text (nested item "kind")
              Phase = text (nested item "phase")
              ExternalId = bindingField binding "externalId"
              IncarnationId = bindingField binding "incarnationId"
              Outcome = optionalText (text (nested item "outcome"))
              Reason = optionalText (text (nested item "reason"))
              CancellationRequested =
                not (isNull (nested item "cancellationRequested"))
                && unbox<bool> (nested item "cancellationRequested") })

    let parseBlockers (raw: obj) : OwnershipBlocker list =
        asArray raw |> Array.choose parseBlockerItem |> Array.toList

    let createOwnership (registryId: string) (records: string array) : obj =
        createNew Ownership (registryId, records)

    let executeOwnershipInMemory (ownership: obj) (requestId: string) (message: obj) : HostReceipt =
        let reply: obj = ownership?execute (requestId, message)
        let status = text (nested reply "status")
        let normalized = if status = "Applied" then "Committed" else status
        let result = nested reply "result"
        let kind = if isRecord result then text (result?kind) else ""
        let revisionValue = nested reply "revision"

        let revision =
            if isNull revisionValue then
                0.
            else
                unbox<float> revisionValue

        { RequestId = requestId
          Status = normalized
          Kind = kind
          Reason = ""
          Revision = revision
          Result = result }

    let ownershipBlockersInMemory (ownership: obj) (scopeId: string) : OwnershipBlocker list =
        try
            parseBlockers (ownership?blockers scopeId)
        with _ ->
            []

    let ownershipScopePhaseInMemory (ownership: obj) (scopeId: string) : string option =
        try
            let view: obj = ownership?scope scopeId
            optionalText (text (nested view "phase"))
        with _ ->
            None

    let private ownershipStream (registryId: string) : string = "ownership:" + registryId

    let private useOwnershipJournal (gitCommonDir: string) (body: obj -> 'a) : 'a = useWritableJournal gitCommonDir body

    let private revisionOrDefault (fallback: float) (value: obj) : float =
        if isNull value then fallback else unbox<float> value

    let private resultKind (result: obj) : string =
        if isRecord result then text (result?kind) else ""

    let private defaultUnknownReason (ackReason: string) : string =
        if ackReason = "" then
            "Unknown journal acknowledgement"
        else
            ackReason

    let private interpretOwnershipAck
        (journal: obj)
        (stream: string)
        (registryId: string)
        (requestId: string)
        (message: obj)
        (expected: float)
        (revision: float)
        (result: obj)
        (kind: string)
        (ack: obj)
        : HostReceipt =
        let ackStatus = text (nested ack "status")
        let ackReason = text (nested ack "reason")
        let committedRev = revisionOrDefault expected (nested ack "revision")

        if ackStatus = "Committed" && committedRev = revision then
            { RequestId = requestId
              Status = "Committed"
              Kind = kind
              Reason = ""
              Revision = revision
              Result = result }
        elif ackStatus = "Replayed" then
            let reread: string array = unbox (journal?read stream)
            let replay = createOwnership registryId reread
            let replayed: obj = replay?execute (requestId, message)
            let repResult = nested replayed "result"

            { RequestId = requestId
              Status = "Replayed"
              Kind = resultKind repResult
              Reason = ""
              Revision = committedRev
              Result = repResult }
        elif ackStatus = "Conflict" || ackStatus = "NotWritten" then
            { RequestId = requestId
              Status = ackStatus
              Kind = ""
              Reason = ackReason
              Revision = expected
              Result = null }
        else
            { RequestId = requestId
              Status = "Unknown"
              Kind = ""
              Reason = defaultUnknownReason ackReason
              Revision = expected
              Result = null }

    let private commitOwnershipRecord
        (journal: obj)
        (stream: string)
        (registryId: string)
        (requestId: string)
        (message: obj)
        (revision: float)
        (result: obj)
        (kind: string)
        (record: string)
        : HostReceipt =
        let expected = revision - 1.

        try
            let ack: obj = journal?append (stream, expected, requestId, record)
            interpretOwnershipAck journal stream registryId requestId message expected revision result kind ack
        with error ->
            { RequestId = requestId
              Status = "Unknown"
              Kind = ""
              Reason = error.Message
              Revision = expected
              Result = null }

    let executeOwnershipDurable
        (gitCommonDir: string)
        (registryId: string)
        (requestId: string)
        (message: obj)
        : HostReceipt =
        let stream = ownershipStream registryId

        useOwnershipJournal gitCommonDir (fun journal ->
            let records: string array = unbox (journal?read stream)
            let candidate = createOwnership registryId records
            let proposal: obj = candidate?execute (requestId, message)
            let status = text (nested proposal "status")
            let result = nested proposal "result"
            let kind = if isRecord result then text (result?kind) else ""
            let revValue = nested proposal "revision"
            let revision = if isNull revValue then 0. else unbox<float> revValue

            if status <> "Applied" then
                { RequestId = requestId
                  Status = status
                  Kind = kind
                  Reason = ""
                  Revision = revision
                  Result = result }
            else
                let record = text (nested proposal "record")
                commitOwnershipRecord journal stream registryId requestId message revision result kind record)

    let mayStartResource (receipt: HostReceipt) : OwnershipLease option =
        if receipt.Status = "Committed" && receipt.Kind = "Reserved" then
            parseLease (nested receipt.Result "lease")
        else
            None

    let ownershipBlockersDurable (gitCommonDir: string) (registryId: string) (scopeId: string) : OwnershipBlocker list =
        if String.IsNullOrWhiteSpace gitCommonDir || String.IsNullOrWhiteSpace registryId then
            []
        else
            let stream = ownershipStream registryId

            useReadableJournal gitCommonDir (fun journal ->
                let records: string array = unbox (journal?read stream)
                let candidate = createOwnership registryId records
                ownershipBlockersInMemory candidate scopeId)
            |> Option.defaultValue []
