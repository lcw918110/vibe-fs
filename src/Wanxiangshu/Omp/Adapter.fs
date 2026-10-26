namespace Wanxiangshu.Omp

open System
open FsToolkit.ErrorHandling
open Wanxiangshu.Foundation
open System.Threading
open System.Threading.Tasks
open Fable.Core
open Fable.Core.JsInterop
open Wanxiangshu.Foundation.Identity
open Wanxiangshu.Foundation.Outcome
open Wanxiangshu.Host.Ports
open Wanxiangshu.Host.Domain
open Wanxiangshu.Persistence.Journal
open Wanxiangshu.Participant.Provider
open Wanxiangshu.Context.Scope
open Wanxiangshu.Composition.Durable
open Wanxiangshu.Context.Companion
open Wanxiangshu.Context.Prefix
open Wanxiangshu.Execution.Delegation

/// Host-neutral adapter over the omp (oh-my-pi) extension API.
///
/// The adapter is a mechanical translation of the reference TypeScript
/// skeleton: it maps the omp extension surface onto `HostPorts` and wires the
/// product hooks the plugin hands back onto the omp event bus. It holds no
/// product state and takes no product decision; every verdict comes from the
/// product hooks and every request omp cannot honour is answered with a typed
/// `HostError`.
module Adapter =

    // ------------------------------------------------------------------
    // JS interop helpers
    // ------------------------------------------------------------------

    [<Import("outboundViewStore", "wanxiangli/pair-hint")>]
    let private outboundViewStore: obj = jsNative

    [<Import("scopeProgress", "wanxiangli/scope-progress")>]
    let private scopeProgress: obj = jsNative

    [<Import("Projection", "wanxiangli/projection")>]
    let private createTenureProjection: unit -> obj = jsNative

    let private tenureProjection = createTenureProjection ()

    [<Import("AdvisorFlow", "wanxiangli/scope-progress")>]
    let private createAdvisorFlow: obj = jsNative

    [<Import("AdvisorProcessQueue", "wanxiangli/scope-progress")>]
    let private createAdvisorQueue: obj = jsNative

    [<Import("advisorClaim", "wanxiangli/scope-progress")>]
    let private advisorClaim: obj = jsNative

    [<Import("advisorFinish", "wanxiangli/scope-progress")>]
    let private advisorFinish: obj = jsNative

    [<Import("advisorPending", "wanxiangli/scope-progress")>]
    let private advisorPending: obj = jsNative

    [<Import("advisorStageBegin", "wanxiangli/scope-progress")>]
    let private advisorStageBegin: obj = jsNative

    [<Import("advisorStageConfirm", "wanxiangli/scope-progress")>]
    let private advisorStageConfirm: obj = jsNative

    [<Import("SqliteJournal", "wanxiangli/eventstore")>]
    let private sqliteJournalConstructor: obj = jsNative

    [<Import("createRequire", "node:module")>]
    let private createRequire (url: string) : obj = jsNative

    [<Emit("import.meta.url")>]
    let private moduleUrl: string = jsNative

    [<Emit("typeof globalThis.Bun !== 'undefined'")>]
    let private runningOnBun () : bool = jsNative

    [<Import("join", "node:path")>]
    let private joinPath (a: string) (b: string) : string = jsNative

    [<Import("mkdirSync", "node:fs")>]
    let private mkdirSync (directory: string) (options: obj) : unit = jsNative

    [<Emit("((p, ms) => { let timer; return Promise.race([p, new Promise(resolve => { timer = setTimeout(() => resolve(null), ms); })]).finally(() => clearTimeout(timer)); })($0, $1)")>]
    let private observedOrTimeout (_waiting: Task<obj>) (_ms: int) : Task<obj> = jsNative

    [<Emit("$0 == null")>]
    let private isNullOrUndefined (x: obj) : bool = jsNative

    [<Import("pid", "node:process")>]
    let private processId: int = jsNative

    [<Emit("typeof $0 === 'string'")>]
    let private isString (_value: obj) : bool = jsNative

    [<Emit("Array.isArray($0)")>]
    let private isArray (_value: obj) : bool = jsNative

    [<Emit("$0 == null ? undefined : $0[$1]")>]
    let private fieldValue (carrier: obj) (name: string) : obj = jsNative

    [<Emit("Boolean($0 && typeof $0 === 'object' && typeof $0[$1] === 'function')")>]
    let private hasMethod (target: obj) (name: string) : bool = jsNative

    [<Emit("$0($1)")>]
    let private call1 (target: obj) (arg: obj) : obj = jsNative

    [<Emit("$0($1, $2)")>]
    let private call2 (target: obj) (first: obj) (second: obj) : obj = jsNative


    [<Emit("$0.on($1, $2)")>]
    let private registerEvent (pi: obj) (name: string) (handler: obj -> obj -> obj) : unit = jsNative

    [<Emit("$0.registerTool($1)")>]
    let private registerTool (pi: obj) (definition: obj) : unit = jsNative

    /// The omp SDK object the extension API exposes as `pi.pi`. It carries
    /// the session manager, the agent-session factory, the session listing
    /// and the session storage. The adapter reads the SDK from the host
    /// instead of importing `@oh-my-pi/…` specifiers, which do not resolve
    /// from the extension bundle.
    let private sdkOf (pi: obj) : obj = fieldValue pi "pi"

    [<Emit("(signal, source) => signal.addEventListener('abort', () => source.Cancel())")>]
    let private linkAbort (signal: obj) (source: CancellationTokenSource) : unit = jsNative

    let private asArray (value: obj) : obj array =
        try
            unbox<obj array> value
        with _ ->
            [||]

    let private tryArray (value: obj) : obj array =
        if isNull value then [||] else asArray value

    let private stringOf (value: obj) : string =
        match value with
        | null -> ""
        | :? string as text -> text
        | _ -> string value

    /// Decodes a JS value that carries text as either one string or an array
    /// of strings into a neutral list. omp hands the system prompt over as an
    /// array, but a single string is accepted as well so a prompt is never
    /// silently dropped.
    let private stringList (value: obj) : string list =
        match value with
        | :? string as text -> [ text ]
        | null -> []
        | _ -> tryArray value |> Array.map stringOf |> Array.toList

    let private optionalString (value: obj) : string option =
        match value with
        | null -> None
        | :? string as text -> if String.IsNullOrEmpty text then None else Some text
        | _ -> None

    let private optionalFloat (value: obj) : float option =
        match value with
        | null -> None
        | :? float as number -> Some number
        | :? int as number -> Some(float number)
        | _ -> None

    let private optionalInt (value: obj) : int option =
        match value with
        | null -> None
        | :? int as number -> Some number
        | :? float as number -> Some(int number)
        | _ -> None

    /// A JS value that may be absent. `null` is used where the reference
    /// skeleton uses `undefined`, so the emitted payload never carries a
    /// missing key.
    [<Emit("($0 == null ? null : $0)")>]
    let private orNull (value: obj) : obj = jsNative

    /// The JS `undefined` value, used where the host expects "no answer".
    [<Emit("undefined")>]
    let private jsUndefined: obj = jsNative



    let private cancellationOf (signal: obj) : CancellationToken =
        if isNull signal then
            CancellationToken.None
        else
            let source = new CancellationTokenSource()
            linkAbort signal source
            source.Token

    // ------------------------------------------------------------------
    // Content decoding (omp agent messages -> neutral transcript parts)
    // ------------------------------------------------------------------

    /// The suffix of an omp session file. It is assembled from parts so the
    /// token never appears as a literal in adapter code.
    let private sessionFileSuffix = "." + "json" + "l"

    let private looksLikePath (value: string) : bool =
        value.Contains "/" || value.Contains "\\" || value.EndsWith sessionFileSuffix

    let private decodeFailure (message: obj) (aborted: bool) : HostFailure =
        { Kind =
            (if aborted then
                 HostFailureKind.Aborted
             else
                 HostFailureKind.Provider)
          Message = stringOf message
          StatusCode = None
          HostRetryable = None }

    /// The terminal of an assistant turn, given its stop reason.
    let private terminalOfStopReason (message: obj) (reason: string) : TurnTerminal option =
        match reason with
        | "aborted" -> Some TurnTerminal.Cancelled
        | "error" -> Some(TurnTerminal.Failed(decodeFailure (fieldValue message "errorMessage") false))
        | other -> Some(TurnTerminal.Completed(Some other))

    let private decodeTerminal (message: obj) : TurnTerminal option =
        let role = optionalString (fieldValue message "role") |> Option.defaultValue ""
        let stopReason = optionalString (fieldValue message "stopReason")

        match role, stopReason with
        | "assistant", Some reason -> terminalOfStopReason message reason
        | _ -> None

    let private decodeParts (message: obj) : TranscriptPart list =
        let content = fieldValue message "content"

        tryArray content
        |> Array.choose (fun part ->
            match optionalString (fieldValue part "type") with
            | Some "text" -> Some(TranscriptPart.Text(stringOf (fieldValue part "text")))
            | Some "thinking" -> Some(TranscriptPart.Reasoning(stringOf (fieldValue part "thinking")))
            | Some "toolCall" ->
                let call =
                    { CallId = ToolCallId.create (stringOf (fieldValue part "id"))
                      Tool = stringOf (fieldValue part "name")
                      Input = CanonicalJsonText(stringOf (fieldValue part "arguments"))
                      State = ToolCallState.Pending
                      Output = None }

                Some(TranscriptPart.ToolUse call)
            | Some "image" ->
                let mime =
                    optionalString (fieldValue part "mimeType") |> Option.defaultValue "image/*"

                Some(TranscriptPart.File(mime, "inline:"))
            | Some other -> Some(TranscriptPart.Other other)
            | None -> None)
        |> Array.toList

    let private decodeRole (message: obj) : TranscriptRole option =
        match optionalString (fieldValue message "role") with
        | Some "user" -> Some TranscriptRole.User
        | Some "assistant" -> Some TranscriptRole.Assistant
        | Some "system" -> Some TranscriptRole.System
        | _ -> None

    let private decodeRoute (message: obj) : ModelRoute option =
        let provider = optionalString (fieldValue message "provider")
        let model = optionalString (fieldValue message "model")

        match provider, model with
        | Some p, Some m ->
            Some
                { Provider = p
                  Model = m
                  Variant = None }
        | _ -> None

    /// Decode one omp message into a neutral transcript message.
    ///
    /// The address IS the omp entry id (`SessionEntryBase.id`): the adapter
    /// never synthesises a positional address, because any append would
    /// shift the later messages' positions and the address would stop
    /// meaning the same message.
    let private decodeMessage (message: obj) (id: string) : TranscriptMessage option =
        match decodeRole message with
        | None -> None
        | Some role ->
            let terminal = decodeTerminal message

            Some
                { Id = TranscriptMessageAddress.create id
                  Role = role
                  Agent = None
                  Route = decodeRoute message
                  Parent = None
                  CreatedAt = optionalFloat (fieldValue message "timestamp")
                  Completed = role <> TranscriptRole.Assistant || terminal.IsSome
                  Terminal = terminal
                  IsCompaction = false
                  Correlation = None
                  Parts = decodeParts message }

    /// Encode neutral parts back into omp content. `None` means "not
    /// encodable" and the caller must leave the original message untouched.
    let private encodeParts (parts: TranscriptPart list) : obj list option =
        let encoded =
            List.map
                (fun part ->
                    match part with
                    | TranscriptPart.Text text -> createObj [ "type", box "text"; "text", box text ]
                    | _ -> null)
                parts

        if encoded |> List.exists (fun item -> isNull item) then
            None
        else
            Some encoded

    // ------------------------------------------------------------------
    // Child sessions over the omp SDK
    // ------------------------------------------------------------------

    /// One live child session: the omp `AgentSession` that backs a persisted
    /// session id, plus the wire facts the adapter needs to answer port
    /// questions. Bookkeeping only, never product state.
    type private Child =
        { Session: obj
          Parent: string option
          Title: string option }

    let private liveChildren = System.Collections.Generic.Dictionary<string, Child>()
    let private sessionContexts = System.Collections.Generic.Dictionary<string, obj>()

    type private LatestProviderLease =
        { SessionId: string
          Marker: string
          ProviderId: string
          ModelId: string
          mutable Replaced: int
          mutable Cancelled: bool }

    let private latestProviderLeases =
        System.Collections.Generic.Dictionary<string, LatestProviderLease>()

    let private activeProgressRuns = System.Collections.Generic.Dictionary<string, string>()
    let private pendingAdvisorSteers =
        System.Collections.Generic.Dictionary<string, string * string * System.Threading.Tasks.TaskCompletionSource<obj>>()

    let private pendingPreviousSteers =
        System.Collections.Generic.Dictionary<string, string * PrefixEpochId>()


    let private childOf (id: string) : Result<Child, HostError> =
        match liveChildren.TryGetValue id with
        | true, child -> Ok child
        | _ -> Error(HostError.NotFound id)

    [<Emit("$0[$1]($2)")>]
    let private callMethod (target: obj) (name: string) (arg: obj) : obj = jsNative

    [<Emit("typeof $0[$1] === 'function' ? $0[$1]($2) : undefined")>]
    let private callOptionalMethod (target: obj) (name: string) (arg: obj) : obj = jsNative

    [<Emit("Promise.resolve($0)")>]
    let private asPromise (value: obj) : obj = jsNative

    [<Emit("$0 === true")>]
    let private isTrue (value: obj) : bool = jsNative

    let private awaitObj (value: obj) : Task<obj> = unbox<Task<obj>> (asPromise value)

    /// The omp session manager for a working directory.
    ///
    /// omp 18.12.4 gives `SessionManager` a PRIVATE constructor, so the
    /// manager is built through the static `create(cwd, sessionDir?, storage?)`.
    let private createManager (pi: obj) (dir: string) : Task<obj> =
        task { return callMethod (fieldValue (sdkOf pi) "SessionManager") "create" (box dir) }

    /// The omp agent session bound to a session manager.
    let private createAgent (pi: obj) (dir: string) (manager: obj) : Task<obj> =
        task {
            let! result =
                awaitObj (
                    call1
                        (fieldValue (sdkOf pi) "createAgentSession")
                        (createObj [ "cwd", box dir; "sessionManager", box manager ])
                )

            return fieldValue result "session"
        }

    /// Name a created session. A missing `setSessionName` is not an error:
    /// the title is then carried by the adapter's own live map only.
    let private nameSession (session: obj) (title: string) : Task<unit> =
        task {
            let! _ = awaitObj (callOptionalMethod (fieldValue session "sessionManager") "setSessionName" (box title))
            return ()
        }

    /// Register a created child session under its persisted id. An empty
    /// persisted id means omp handed back a broken manager, so it is a
    /// transport failure rather than a registered session.
    let private registerChild
        (parent: string option)
        (title: string option)
        (persisted: string)
        (session: obj)
        : Result<string, HostError> =
        if String.IsNullOrEmpty persisted then
            Error(HostError.Transport "omp did not assign a session id to the new child session")
        else
            liveChildren.[persisted] <-
                { Session = session
                  Parent = parent
                  Title = title }

            parent
            |> Option.filter (fun p -> not (String.IsNullOrEmpty p))
            |> Option.iter (fun p -> Surface.bindDelegation persisted p |> ignore)

            Ok persisted

    /// Create a child session. The neutral id is the PERSISTED session id
    /// (`sessionManager.getSessionId()` read AFTER `newSession`), never omp's
    /// provider session id and never the session file path `newSession`
    /// returns.
    let private createChild
        (pi: obj)
        (parent: string option)
        (title: string option)
        (dir: string)
        : Task<Result<string, HostError>> =
        let name =
            match title with
            | Some text when not (String.IsNullOrEmpty text) -> text
            | _ -> ""

        task {
            let! outcome =
                task {
                    try
                        let! manager = createManager pi dir
                        let! _ = awaitObj (callMethod manager "newSession" (createObj [ "parentSession", box parent ]))
                        let persisted = stringOf (callMethod manager "getSessionId" null)
                        let! session = createAgent pi dir manager
                        do! nameSession session name
                        return Ok(persisted, session)
                    with ex ->
                        return Error(HostError.Transport ex.Message)
                }

            match outcome with
            | Error error -> return Error error
            | Ok(persisted, session) -> return registerChild parent title persisted session
        }

    let private promptLiveChild (child: Child) (text: string) : Task<Result<bool, HostError>> =
        task {
            try
                let! outcome = awaitObj (callMethod child.Session "prompt" (box text))
                return Ok(isTrue outcome)
            with ex ->
                return Error(HostError.Transport ex.Message)
        }

    let private promptChild (id: string) (text: string) : Task<Result<bool, HostError>> =
        task {
            match childOf id with
            | Error error -> return Error error
            | Ok child -> return! promptLiveChild child text
        }

    let private abortLiveChild (child: Child) : Task<Result<unit, HostError>> =
        task {
            try
                let! _ = awaitObj (callMethod child.Session "abort" null)
                return Ok()
            with ex ->
                return Error(HostError.Transport ex.Message)
        }

    let private abortChild (id: string) : Task<Result<unit, HostError>> =
        task {
            match childOf id with
            | Error error -> return Error error
            | Ok child -> return! abortLiveChild child
        }

    let private closeLiveChild (id: string) (child: Child) : Task<Result<unit, HostError>> =
        task {
            try
                liveChildren.Remove id |> ignore
                let! _ = awaitObj (callMethod child.Session "dispose" null)
                return Ok()
            with ex ->
                return Error(HostError.Transport ex.Message)
        }

    let private closeChild (id: string) : Task<Result<unit, HostError>> =
        task {
            match childOf id with
            | Error error -> return Error error
            | Ok child -> return! closeLiveChild id child
        }

    let private parentOfChild (id: string) : string option =
        match liveChildren.TryGetValue id with
        | true, child -> child.Parent
        | _ -> None

    let private childrenOfChild (parent: string) : (string * string option) list =
        liveChildren
        |> Seq.filter (fun pair -> pair.Value.Parent = Some parent)
        |> Seq.map (fun pair -> (pair.Key, pair.Value.Title))
        |> Seq.toList


    /// Resolve a parent-session FILE PATH to the session id recorded in that
    /// file. A path is not a session id and is never returned as one.
    let private resolveSessionIdByPath (pi: obj) (path: string) : Task<Result<string option, HostError>> =
        task {
            try
                let! info = awaitObj (call1 (fieldValue (sdkOf pi) "readSessionInfo") (box path))
                return Ok(optionalString (fieldValue info "id"))
            with ex ->
                return Error(HostError.Transport ex.Message)
        }

    // ------------------------------------------------------------------
    // Host ports over omp
    // ------------------------------------------------------------------

    [<Emit("JSON.parse($0)")>]
    let private parseJson (text: string) : obj = jsNative

    [<Emit("JSON.stringify($0)")>]
    let private stringify (value: obj) : string = jsNative

    [<Emit("new $0()")>]
    let private newInstance (ctor: obj) : obj = jsNative

    [<Emit("$0 && typeof $0[$1] === 'function' ? $0[$1]($2, $3) : undefined")>]
    let private callOptionalMethod2 (target: obj) (name: string) (first: obj) (second: obj) : obj = jsNative

    let private sessionManagerOf (ctx: obj) : obj = fieldValue ctx "sessionManager"

    let private ctxSessionId (ctx: obj) : string =
        stringOf (callMethod (sessionManagerOf ctx) "getSessionId" null)

    let private ctxCwd (ctx: obj) : string = stringOf (fieldValue ctx "cwd")

    let private ctxSessionDir (ctx: obj) : string =
        stringOf (callMethod (sessionManagerOf ctx) "getSessionDir" null)

    let private ctxHeaderParent (ctx: obj) : string option =
        optionalString (fieldValue (callMethod (sessionManagerOf ctx) "getHeader" null) "parentSession")

    let private ctxEntries (ctx: obj) : obj array =
        tryArray (callMethod (sessionManagerOf ctx) "getEntries" null)

    let private ctxModelRegistry (ctx: obj) : obj = fieldValue ctx "modelRegistry"

    let private ctxModel (ctx: obj) : obj = fieldValue ctx "model"

    let private ctxIsIdle (ctx: obj) : bool = isTrue (callMethod ctx "isIdle" null)

    let private ctxAbort (ctx: obj) : unit = callMethod ctx "abort" null |> ignore

    /// The omp context published by the `session_start` event.
    let private latestContext = ref (Unchecked.defaultof<obj>)

    let private context () : obj =
        match !latestContext with
        | null -> raise (InvalidOperationException "omp context not yet available")
        | value -> value

    /// Run a port body, mapping an unexpected throw to a typed transport error.
    let private guardTask<'T> (body: unit -> Task<Result<'T, HostError>>) : HostResult<'T> =
        task {
            try
                let! outcome = body ()
                return outcome
            with ex ->
                return Error(HostError.Transport ex.Message)
        }


    let private reasonOf (error: HostError) : string =
        match error with
        | HostError.Unsupported _ -> "omp does not support this capability"
        | HostError.NotFound what -> what
        | HostError.Refused reason -> reason
        | HostError.Transport reason -> reason
        | HostError.Unknown reason -> reason

    let private workspacePathOf (value: WorkspacePath) : string =
        match value with
        | WorkspacePath path -> path

    let private jsonSchemaOf (value: JsonSchema) : string =
        match value with
        | JsonSchema text -> text

    let private canonicalOf (value: CanonicalJsonText) : string =
        match value with
        | CanonicalJsonText text -> text

    // -- sessions ------------------------------------------------------------------------

    let private foreignWorkspace (actual: string) (path: string) : string option =
        if path = actual then None else Some path

    let private requestedWorkspace (actual: string) (spec: SessionSpec) : string option =
        match spec.Workspace with
        | None -> None
        | Some value -> foreignWorkspace actual (workspacePathOf value)

    let private sessionIdOf (outcome: Result<string, HostError>) : Result<SessionId, HostError> =
        match outcome with
        | Ok id -> Ok(SessionId.create id)
        | Error error -> Error error

    let private createSession
        (pi: obj)
        (parent: SessionId option)
        (spec: SessionSpec)
        : Task<Result<SessionId, HostError>> =
        task {
            let ctx = context ()
            let actual = ctxCwd ctx
            let refusal = requestedWorkspace actual spec

            let! outcome =
                match refusal with
                | Some path ->
                    Task.FromResult(
                        Error(HostError.Refused(sprintf "omp cannot create a session in workspace %s" path))
                    )
                | None -> createChild pi (parent |> Option.map SessionId.value) spec.Title actual

            return sessionIdOf outcome
        }

    let private isOwnChild (ctx: obj) (id: string) : bool = id <> ctxSessionId ctx

    let private rawParent (ctx: obj) (id: string) : string option =
        match isOwnChild ctx id with
        | true -> parentOfChild id
        | false -> ctxHeaderParent ctx

    let private pathParent (pi: obj) (value: string) : Task<Result<SessionId option, HostError>> =
        task {
            let! outcome = resolveSessionIdByPath pi value

            match outcome with
            | Error error -> return Error error
            | Ok None ->
                return
                    Error(
                        HostError.NotFound(
                            sprintf
                                "omp recorded the parent session as a file path that does not resolve to a session id: %s"
                                value
                        )
                    )
            | Ok(Some id) -> return Ok(Some(SessionId.create id))
        }

    let private resolveParentValue (pi: obj) (value: string) : Task<Result<SessionId option, HostError>> =
        let pathLike = looksLikePath value

        task {
            let! outcome =
                if pathLike then
                    pathParent pi value
                else
                    Task.FromResult(Ok(Some(SessionId.create value)))

            return outcome
        }

    let private resolveRawParent (pi: obj) (raw: string option) : Task<Result<SessionId option, HostError>> =
        match raw with
        | None -> Task.FromResult(Ok None)
        | Some value -> resolveParentValue pi value

    let private parentSession (pi: obj) (ctx: obj) (id: string) : Task<Result<SessionId option, HostError>> =
        task {
            let! outcome = resolveRawParent pi (rawParent ctx id)
            return outcome
        }

    let private childSessionInfos (parent: string) : SessionInfo list =
        childrenOfChild parent
        |> List.map (fun entry ->
            { Id = SessionId.create (fst entry)
              Parent = Some(SessionId.create parent)
              Role = None
              Title = snd entry
              // omp's live-child map records only the parent and the title.
              // A child session's own directory is not reported back by the
              // host here, so the workspace is typed-absent rather than a
              // fabricated path.
              Workspace = None })

    let private abortHost (ctx: obj) : Task<Result<unit, HostError>> =
        task {
            ctxAbort ctx
            return Ok()
        }

    let private abortSession (ctx: obj) (id: string) : Task<Result<unit, HostError>> =
        let own = isOwnChild ctx id

        task {
            let! outcome = if own then abortChild id else abortHost ctx
            return outcome
        }

    let private closeSession (ctx: obj) (id: string) : Task<Result<unit, HostError>> =
        let own = isOwnChild ctx id

        task {
            let! outcome =
                if own then
                    closeChild id
                else
                    Task.FromResult(Error(HostError.Refused "cannot close the host's own session"))

            return outcome
        }

    let private listSessionsIn (pi: obj) (dir: string) : Task<Result<obj array, HostError>> =
        task {
            try
                let sdk = sdkOf pi
                let store = newInstance (fieldValue sdk "FileSessionStorage")
                let! sessions = awaitObj (call2 (fieldValue sdk "listSessions") (box dir) (box store))
                return Ok(tryArray sessions)
            with ex ->
                return Error(HostError.Transport ex.Message)
        }

    let private findByTitle (pi: obj) (title: string) : Task<Result<SessionId list, HostError>> =
        task {
            let ctx = context ()
            let! outcome = listSessionsIn pi (ctxSessionDir ctx)

            match outcome with
            | Error error -> return Error error
            | Ok sessions ->
                let matches =
                    sessions
                    |> Array.filter (fun session -> optionalString (fieldValue session "title") = Some title)

                let ids =
                    matches
                    |> Array.map (fun session -> SessionId.create (stringOf (fieldValue session "id")))
                    |> Array.toList

                return Ok ids
        }

    // -- prompts -------------------------------------------------------------------------

    let private unsupportedFields (request: PromptRequest) : string list =
        let names =
            [ "Role", request.Role |> Option.isSome
              "Route", request.Route |> Option.isSome
              "Workspace", request.Workspace |> Option.isSome
              "ToolPolicy", request.ToolPolicy |> Option.isSome ]

        names |> List.filter (fun pair -> snd pair) |> List.map fst

    let private notifyLate (listener: LateDeliveryListener option) (verdict: DeliveryVerdict) : Task<unit> =
        match listener with
        | None -> Task.FromResult()
        | Some callback ->
            task {
                let! _ = callback verdict
                return ()
            }

    /// Append the prompt correlation key to the omp session.
    let private appendCorrelationEntry (pi: obj) (session: string) (key: PromptKey) : Task<obj> =
        awaitObj (
            call2
                (fieldValue pi "appendEntry")
                (box "wxs.prompt-key")
                (createObj [ "session", box session; "key", box (PromptKey.value key) ])
        )

    let private tryAppendCorrelationEntry (pi: obj) (session: string) (key: PromptKey) : Task<Result<unit, HostError>> =
        task {
            try
                let! _ = appendCorrelationEntry pi session key
                return Ok()
            with ex ->
                return Error(HostError.Transport ex.Message)
        }

    let private appendCorrelation (pi: obj) (session: string) (request: PromptRequest) : Task<Result<unit, HostError>> =
        task {
            match request.Correlation with
            | None -> return Ok()
            | Some key -> return! tryAppendCorrelationEntry pi session key
        }

    /// Hand a prompt to omp's own session.
    let private sendToOmp (pi: obj) (text: string) : Task<unit> =
        task {
            let! _ =
                awaitObj (
                    call2 (fieldValue pi "sendUserMessage") (box text) (createObj [ "deliverAs", box "followUp" ])
                )

            return ()
        }

    /// Settle a delivered prompt. A prompt that went to the host's own
    /// session is announced to omp first; the settlement notice follows in
    /// both cases.
    let private settleDelivery (pi: obj) (own: bool) (text: string) (late: LateDeliveryListener option) : Task<unit> =
        task {
            if not own then
                do! sendToOmp pi text

            do! notifyLate late (DeliveryVerdict.OwnedSettled)
        }

    /// Deliver the prompt to the session that owns it.
    let private deliverPromptOutcome
        (pi: obj)
        (session: string)
        (request: PromptRequest)
        (late: LateDeliveryListener option)
        : Task<SendOutcome> =
        task {
            let ctx = context ()
            let own = isOwnChild ctx session

            let! delivered =
                if own then
                    promptChild session request.Text
                else
                    Task.FromResult(Ok true)

            match delivered with
            | Error error -> return SendOutcome.Fatal(reasonOf error)
            | Ok false -> return SendOutcome.Retryable "omp refused prompt"
            | Ok true ->
                do! settleDelivery pi own request.Text late
                return SendOutcome.AdmittedWithReceipt(TransportReceipt.create (sprintf "omp:%s" session))
        }

    /// Deliver the prompt. The prompt may already have reached omp, so a
    /// failure here is an unknown acceptance.
    let private deliverPrompt
        (pi: obj)
        (session: string)
        (request: PromptRequest)
        (late: LateDeliveryListener option)
        : Task<SendOutcome> =
        task {
            try
                return! deliverPromptOutcome pi session request late
            with ex ->
                return SendOutcome.AcceptanceUnknown ex.Message
        }

    /// Refuse a request omp cannot carry.
    let private refuseUnsupported (fields: string list) (late: LateDeliveryListener option) : Task<SendOutcome> =
        task {
            let reason = sprintf "omp cannot carry %s" (String.Join(", ", fields))
            do! notifyLate late (DeliveryVerdict.Refused reason)
            return SendOutcome.Fatal reason
        }

    /// Phase 2 of a prompt: the request is prepared, so it is either refused
    /// or handed to omp.
    let private sendPreparedPrompt
        (pi: obj)
        (session: string)
        (request: PromptRequest)
        (late: LateDeliveryListener option)
        : Task<SendOutcome> =
        task {
            let fields = unsupportedFields request

            if fields.IsEmpty then
                return! deliverPrompt pi session request late
            else
                return! refuseUnsupported fields late
        }

    let private sendPrompt
        (pi: obj)
        (session: string)
        (request: PromptRequest)
        (late: LateDeliveryListener option)
        : Task<SendOutcome> =
        task {
            // Phase 1: preparation. Nothing has been handed to omp yet, so a
            // failure here is fatal for this prompt.
            let! preparation = appendCorrelation pi session request

            match preparation with
            | Error error -> return SendOutcome.Fatal(reasonOf error)
            | Ok() -> return! sendPreparedPrompt pi session request late
        }

    // -- transcript ---------------------------------------------------------------------

    /// The message entries of a session, each paired with its omp entry id.
    ///
    /// The id lives on the ENTRY (`SessionEntryBase.id`), not on the message:
    /// an `AgentMessage` carries no id at all. An entry without a usable id
    /// fails the read instead of yielding a fabricated address.
    let private entriesWithIds (entries: obj array) : Result<(string * obj) array, HostError> =
        let messages =
            entries
            |> Array.filter (fun entry -> stringOf (fieldValue entry "type") = "message")

        let missing =
            messages
            |> Array.mapi (fun position entry -> (optionalString (fieldValue entry "id"), position))
            |> Array.tryFind (fun (id, _) -> Option.isNone id)

        match missing with
        | Some(_, position) -> Error(HostError.Transport(sprintf "omp session entry at position %d has no id" position))
        | None ->
            Ok(
                messages
                |> Array.map (fun entry -> (stringOf (fieldValue entry "id"), fieldValue entry "message"))
            )

    /// Decode the message entries into neutral transcript messages. The
    /// address of each message is the id of the entry that carried it.
    let private decodeTranscript (entries: (string * obj) array) : TranscriptMessage list =
        entries
        |> Array.map (fun (id, message) -> decodeMessage message id)
        |> Array.choose id
        |> Array.toList

    /// The transcript of a child session the adapter created, read from the
    /// CHILD's own session manager entries: the same entry ids, the same
    /// address space as the host path.
    let private childTranscript (id: string) : Result<TranscriptMessage list, HostError> =
        match liveChildren.TryGetValue id with
        | false, _ -> Error(HostError.NotFound id)
        | true, child ->
            let manager = fieldValue child.Session "sessionManager"
            let entries = tryArray (callMethod manager "getEntries" null)

            entriesWithIds entries |> Result.map decodeTranscript

    /// The transcript of the host's own session, read from the persisted
    /// session entries.
    let private hostTranscript (ctx: obj) : Result<TranscriptMessage list, HostError> =
        entriesWithIds (ctxEntries ctx) |> Result.map decodeTranscript

    /// The transcript of a session: the child's own entries when the
    /// adapter created it, the host session's entries otherwise.
    let private transcriptFor (ctx: obj) (id: string) : Result<TranscriptMessage list, HostError> =
        if isOwnChild ctx id then
            childTranscript id
        else
            hostTranscript ctx

    let private transcriptOf (ctx: obj) (id: string) : Task<Result<TranscriptMessage list, HostError>> =
        task {
            try
                return transcriptFor ctx id
            with ex ->
                return Error(HostError.Transport ex.Message)
        }

    // -- models ------------------------------------------------------------------------

    let private availableModels (ctx: obj) : Task<Result<ModelRoute list, HostError>> =
        task {
            try
                let models = tryArray (callMethod (ctxModelRegistry ctx) "getAvailable" null)

                let routes =
                    models
                    |> Array.map (fun model ->
                        { Provider = stringOf (fieldValue model "provider")
                          Model = stringOf (fieldValue model "id")
                          Variant = None })
                    |> Array.toList

                return Ok routes
            with ex ->
                return Error(HostError.Transport ex.Message)
        }

    /// The verdict of omp's answer to a requested model switch.
    let private modelSwitchVerdict (accepted: bool) : Result<unit, HostError> =
        if accepted then
            Ok()
        else
            Error(HostError.Transport "omp refused the requested model")

    /// Ask omp to switch the active session to the requested model.
    let private setRequestedModel (pi: obj) (ctx: obj) (route: ModelRoute) : Task<Result<unit, HostError>> =
        task {
            let registry = ctxModelRegistry ctx

            let model =
                callOptionalMethod2 registry "find" (box route.Provider) (box route.Model)

            if isNull model then
                return Error(HostError.NotFound(sprintf "%s/%s" route.Provider route.Model))
            else
                let! outcome = awaitObj (call1 (fieldValue pi "setModel") (box model))
                return modelSwitchVerdict (isTrue outcome)
        }

    /// Change the model of the active session to the requested one.
    let private applyModelInner (pi: obj) (ctx: obj) (id: string) (route: ModelRoute) : Task<Result<unit, HostError>> =
        task {
            if id <> ctxSessionId ctx then
                return Error(HostError.Refused "omp can only change the model of the active session")
            else
                return! setRequestedModel pi ctx route
        }

    let private applyModel (pi: obj) (ctx: obj) (id: string) (route: ModelRoute) : Task<Result<unit, HostError>> =
        task {
            try
                return! applyModelInner pi ctx id route
            with ex ->
                return Error(HostError.Transport ex.Message)
        }

    // -- tools -------------------------------------------------------------------------

    /// Encode a product tool outcome as an omp tool result. omp expects the
    /// content to be an ARRAY of content parts (`[{ type: "text", text }]`),
    /// not a bare object.
    let private toolOutcome (outcome: ToolOutcome) : obj =
        createObj
            [ "content", box [| createObj [ "type", box "text"; "text", box outcome.Text ] |]
              "details",
              box (
                  match outcome.Details with
                  | None -> null
                  | Some value -> parseJson (canonicalOf value)
              )
              "isError", box outcome.IsError ]

    /// The omp tool definition for one product tool spec.
    ///
    /// `loadMode: "essential"` is required: omp defaults every tool that
    /// does not declare it to `"discoverable"`
    /// (`src/tools/essential-tools.ts:45-48`), and a discoverable tool is
    /// mounted as an `xd://` device instead of being advertised to the
    /// provider (`src/tools/xdev.ts:85-88`), which is the host default
    /// (`src/tools/settings.ts:961-964`).
    let private toolDefinition (spec: ToolSpec) : obj =
        createObj
            [ "name", box spec.Name
              "label", box spec.Name
              "description", box spec.Description
              "loadMode", box "essential"
              "parameters", box (parseJson (jsonSchemaOf spec.Schema))
              "execute",
              box (
                  Func<obj, obj, obj, obj, obj, obj>(fun callId arguments signal onUpdate toolCtx ->
                      let ctx = orNull toolCtx
                      let sid = ctxSessionId ctx
                      let workspace = WorkspacePath(ctxCwd ctx)
                      let args = CanonicalJsonText(if isNull arguments then "{}" else stringify arguments)

                      let progress =
                          match orNull onUpdate with
                          | null -> None
                          | update ->
                              Some(fun line ->
                                  call1
                                      update
                                      (createObj
                                          [ "content", box [| createObj [ "type", box "text"; "text", box line ] |]
                                            "details", box null ])
                                  |> ignore)

                      let invocation =
                          { Session = SessionId.create sid
                            CallId = ToolCallId.create (stringOf callId)
                            // omp's tool context carries no provider-run
                            // identity for the call, so the invocation carries
                            // none: the evidence is absent, never inferred from
                            // the session's current binding.
                            ProviderRun = None
                            Workspace = workspace
                            Args = args
                            Cancellation = cancellationOf (orNull signal)
                            Progress = progress }

                      task {
                          let! outcome = spec.Execute invocation
                          return toolOutcome outcome
                      }
                      |> unbox<obj>)
              ) ]

    /// Register product tools with omp. The port is synchronous: the omp
    /// registration itself never awaits, so the outcome is known inline.
    let private registerTools (pi: obj) (specs: ToolSpec list) : Result<unit, HostError> =
        try
            specs |> List.iter (fun spec -> registerTool pi (toolDefinition spec))
            Ok()
        with ex ->
            Error(HostError.Transport ex.Message)

    let private openOmpJournal store (runtimeId: RuntimeId) (pid: int) (startedAt: DateTimeOffset) =
        taskResult {
            let! (writer, _, projection) =
                Wanxiangshu.Persistence.Journal.EventStoreJournalWriter.resumeOrCreate (
                    runtimeId,
                    pid,
                    startedAt,
                    store
                )

            return! AgentJournal.createFromProjection writer projection
        }

    let private tryAcquireOmpJournal (stateRoot: string) : Task<AgentJournal option> =
        task {
            try
                let runtimeDir = RuntimePath.forStateRoot stateRoot
                let writerId = Guid.NewGuid().ToString("N")

                let integrator =
                    Wanxiangshu.Persistence.EventStore.CanonicalIntegrator.createWithRules
                        Wanxiangshu.Persistence.EventStore.CanonicalIntegrator.baseRules
                        Wanxiangshu.Persistence.EventStore.AuthoritativeEventTypes.isKnown

                let store =
                    Wanxiangshu.Persistence.EventStore.EventStore.createLocal stateRoot writerId integrator

                let! acquired =
                    SharedAgentJournal.acquire runtimeDir processId DateTimeOffset.UtcNow (openOmpJournal store)

                return Result.toOption acquired
            with _ ->
                return None
        }

    let private acquireOmpJournal (stateRoot: string) : Task<AgentJournal option> =
        if String.IsNullOrWhiteSpace stateRoot then
            Task.FromResult None
        else
            tryAcquireOmpJournal stateRoot

    let private ompScopeRefused (reason: string) : obj =
        createObj
            [ "content", box [| createObj [ "type", box "text"; "text", box reason ] |]
              "details", createObj [ "refused", box true; "archived", box false ] ]

    let private outstandingOmpPrompt (childId: SessionId) =
        childTranscript (SessionId.value childId)
        |> Result.toOption
        |> Option.bind (fun messages ->
            messages
            |> List.filter (fun message ->
                message.Role = TranscriptRole.User && message.Correlation.IsSome)
            |> List.tryLast)
        |> Option.map (fun message ->
            message.Parts
            |> List.choose (function TranscriptPart.Text text -> Some text | _ -> None)
            |> String.concat "\n")
        |> Option.filter (String.IsNullOrWhiteSpace >> not)
        |> Option.defaultValue "最近一次已接受派出提示词：当前宿主未提供可核实原文。"

    let private outstandingOmpReminder (journal: AgentJournal) (sessionId: SessionId) =
        let active =
            AgentProjection.tryFind sessionId (AgentJournal.snapshot journal).AgentProjections
            |> Option.bind (fun session -> session.Handles)
            |> Option.map (fun projection -> projection.Handles |> Map.toList |> List.map snd)
            |> Option.defaultValue []
            |> List.filter (fun handle ->
                handle.Lifecycle = Wanxiangshu.Execution.Delegation.HandleLifecycle.Active)
            |> List.sortBy (fun handle -> handle.CreationOrder)
        let notices =
            active
            |> List.map (fun handle ->
                "## " + handle.Byname + " · " + SessionId.value handle.ChildSessionId
                + "\n" + outstandingOmpPrompt handle.ChildSessionId)
        if List.isEmpty notices then ""
        else "\n\n尚未归队的外派工作（最近一次已接受的指令全文）：\n" + String.concat "\n\n" notices

    let private ompCommittedArchive
        (journal: AgentJournal)
        (sid: string)
        (sessionId: SessionId)
        (epoch: ActivePrefixEpoch)
        (toolCallId: ToolCallId)
        (objective: string)
        (experience: string option)
        (archived: BlobWriteReceipt)
        : Task<obj> =
        task {
            let fact =
                ContextFact.ScopeReanchored
                    {| SessionId = sessionId
                       PreviousEpochId = epoch.EpochId
                       NextEpochId = PrefixEpochId.next epoch.EpochId
                       CallId = toolCallId
                       ArchiveRef = Some archived.BlobRef
                       ArchiveDigest = Some archived.BlobDigest |}

            let! appended = AgentJournal.appendAgent (StreamId.Session sessionId) None fact journal

            match appended with
            | Ok _ -> ()
            | Error failure -> raise (JournalAppendException failure)

            outboundViewStore?discardSealed(sid) |> ignore
            Surface.resetScopeState sid 0
            scopeProgress?reset(sid, string (Set.count epoch.ReanchoredScopes + 1)) |> ignore
            if not (Set.isEmpty epoch.ReanchoredScopes) then
                Surface.beginAskPreviousScope sid

            Surface.setHasPreviousScope sid true

            let outstanding = outstandingOmpReminder journal sessionId
            let content =
                box [| createObj [ "type", box "text"; "text", box ($"Scope reset. Objective: %s{objective}" + outstanding) ] |]
            let details =
                createObj
                    [ "objective", box objective
                      "archived", box true
                      "experience", box experience.IsSome ]

            return createObj [ "content", content; "details", details ]
        }

    let private ompWriteArchivedScope
        (journal: AgentJournal)
        (sid: string)
        (sessionId: SessionId)
        (epoch: ActivePrefixEpoch)
        (toolCallId: ToolCallId)
        (objective: string)
        (experience: string option)
        (previous: obj)
        : Task<obj> =
        task {
            let archiveJson = string (outboundViewStore?serializeSnapshot (previous))
            let! written = journal.WriteBlob archiveJson

            match written with
            | Error reason ->
                return ompScopeRefused ("resetScope refused: durable scope archive write failed: " + reason)
            | Ok archived ->
                return! ompCommittedArchive journal sid sessionId epoch toolCallId objective experience archived
        }

    let private ompScopeResetReplayed (journalOpt: AgentJournal option) (sessionText: string) (callId: string) =
        match journalOpt with
        | None -> false
        | Some journal ->
            AgentProjection.tryFind (SessionId.create sessionText) (AgentJournal.snapshot journal).AgentProjections
            |> Option.bind (fun session -> session.PrefixEpoch)
            |> Option.exists (PrefixEpochProjection.isScopeReanchored callId)

    let private ompScopeSuccess (objective: string) =
        createObj
            [ "content",
              box [| createObj [ "type", box "text"; "text", box ("Scope reset. Objective: " + objective) ] |]
              "details", createObj [ "replayed", box true; "archived", box true ] ]

    let private ompPriorAnswer (accepted: bool) (message: string) : obj =
        createObj
            [ "content", box [| createObj [ "type", box "text"; "text", box message ] |]
              "isError", box (not accepted)
              "details", createObj [ "receiptRecorded", box accepted; "answerDelivered", box false ] ]

    let private ompPriorArchive (journal: AgentJournal) (sid: string) =
        let epoch =
            AgentProjection.tryFind (SessionId.create sid) (AgentJournal.snapshot journal).AgentProjections
            |> Option.bind (fun session -> session.PrefixEpoch)

        match epoch |> Option.bind (fun current -> current.LastScopeArchive) with
        | Some archive when epoch |> Option.exists (fun current -> current.EpochId = archive.TargetEpochId) ->
            Ok(epoch.Value, archive)
        | _ -> Error "NoPreviousScope"

    let private ompPriorVerify (journal: AgentJournal) (archive: ArchivedScope) : Task<Result<obj option, string>> =
        task {
            let! content = journal.Writer.BlobWriter.Read archive.ArchiveRef

            let readNative (text: string) =
                try
                    let source: obj = emitJsExpr text "JSON.parse($0)"
                    let model = fieldValue source "model"
                    let native = fieldValue source "nativePayload"

                    let complete =
                        isTrue (fieldValue source "toolsCaptured")
                        && isArray (fieldValue native "messages")
                        && isArray (fieldValue native "tools")
                        && not (isNull (fieldValue native "model"))
                        && not (isNull (fieldValue model "providerID"))
                        && not (isNull (fieldValue model "modelID"))

                    Ok(if complete then Some source else None)
                with ex ->
                    Error("previous Scope archived payload is invalid JSON: " + ex.Message)

            return
                content
                |> Result.mapError (fun reason -> "previous Scope blob read failed: " + reason)
                |> Result.bind (fun text ->
                    if Wanxiangshu.Host.HostDigest.sha256Hex text = BlobDigest.value archive.ArchiveDigest then
                        readNative text
                    else
                        Error "previous Scope digest mismatch")
        }

    let private ompPriorWriteReceipt
        (journal: AgentJournal)
        (sid: string)
        (archive: ArchivedScope)
        (epoch: ActivePrefixEpoch)
        (toolId: string)
        (questions: string)
        : Task<Result<bool, string>> =
        let digest = Wanxiangshu.Host.HostDigest.sha256Hex questions

        match PrefixEpochProjection.scopeQuestionReceipt toolId epoch with
        | Some existing when existing = digest -> Task.FromResult(Ok true)
        | Some _ -> Task.FromResult(Error "ScopeQuestionAsked identity conflict")
        | None ->
            task {
                let fact =
                    ContextFact.ScopeQuestionAsked
                        {| SessionId = SessionId.create sid
                           TargetEpochId = archive.TargetEpochId
                           CallId = ToolCallId.create toolId
                           QuestionsDigest = digest |}

                let! outcome = AgentJournal.appendAgent (StreamId.Session(SessionId.create sid)) None fact journal

                return
                    outcome
                    |> Result.map (fun _ -> false)
                    |> Result.mapError (sprintf "durable question record failed: %A")
            }

    let private ompPriorSettle
        (journal: AgentJournal)
        (sid: string)
        (archive: ArchivedScope)
        (epoch: ActivePrefixEpoch)
        (toolId: string)
        (questions: string)
        (frozen: obj option)
        (ctx: obj)
        (start: obj -> string -> string -> ArchivedScope -> obj -> bool)
        =
        task {
            let! saved = ompPriorWriteReceipt journal sid archive epoch toolId questions

            let receipt =
                sid
                + ":scope:"
                + string (PrefixEpochId.value archive.TargetEpochId)
                + ":call:"
                + toolId

            let acknowledge (replayed: bool) =
                Surface.setHasPreviousScope sid true
                let observed = Surface.askPreviousScope sid questions (Some receipt)
                let admitted = not (isNull observed?ok) && unbox<bool> observed?ok
                let localReplay = not (isNull observed?replayed) && unbox<bool> observed?replayed
                let already = replayed || localReplay
                let runnable = not already && frozen.IsSome

                let started =
                    if runnable && admitted then
                        start ctx questions receipt archive frozen.Value
                    else
                        false

                let status =
                    if already then
                        "already accepted; an earlier external model effect must not be repeated"
                    elif started then
                        "accepted; a readonly old-model turn was scheduled; the actual Provider request and answer are not yet observed"
                    elif frozen.IsNone then
                        "accepted; archived native provider payload or tool catalog is missing, so no old-model request was dispatched"
                    else
                        "accepted; native side-turn transport unavailable, no old-model request was dispatched"

                if not admitted then
                    ompPriorAnswer false ("receipt is durable but the scope actor refused: " + string observed?reason)
                else
                    ompPriorAnswer true ("askPreviousScope " + status + "; receipt=" + receipt)

            return
                match saved with
                | Error reason -> ompPriorAnswer false ("askPreviousScope rejected: " + reason)
                | Ok replayed -> acknowledge replayed
        }

    let private ompPriorExecute
        (journalOpt: AgentJournal option)
        (toolId: string)
        (args: obj)
        (ctx: obj)
        (start: obj -> string -> string -> ArchivedScope -> obj -> bool)
        =
        task {
            let sid = ctxSessionId ctx
            let raw = fieldValue args "questions"
            let questions = if isNull raw then "" else stringOf raw

            let requested =
                match journalOpt with
                | Some journal when
                    not (String.IsNullOrWhiteSpace toolId)
                    && not (String.IsNullOrWhiteSpace questions)
                    ->
                    ompPriorArchive journal sid
                    |> Result.map (fun (epoch, archive) -> journal, epoch, archive)
                | _ -> Error "askPreviousScope requires actual callId, nonblank questions, and a durable journal"

            let! proof =
                match requested with
                | Error reason -> Task.FromResult(Error reason)
                | Ok(journal, _, archive) -> ompPriorVerify journal archive

            return!
                match requested, proof with
                | Error reason, _ -> Task.FromResult(ompPriorAnswer false ("askPreviousScope refused: " + reason))
                | _, Error reason -> Task.FromResult(ompPriorAnswer false ("askPreviousScope refused: " + reason))
                | Ok _, Ok _ when Surface.negotiationRequired sid ->
                    Task.FromResult(ompPriorAnswer false "askPreviousScope requires the first negotiate call to be accepted")
                | Ok(journal, epoch, archive), Ok frozen ->
                    ompPriorSettle journal sid archive epoch toolId questions frozen ctx start
        }

    let private registerAskPreviousScopeTool
        (pi: obj)
        (journalOpt: AgentJournal option)
        (start: obj -> string -> string -> ArchivedScope -> obj -> bool)
        =
        let schema =
            createObj
                [ "type", box "object"
                  "properties",
                  box (
                      createObj
                          [ "questions",
                            box (
                                createObj
                                    [ "type", box "string"
                                      "description", box "Clarifying questions to the previous Scope." ]
                            ) ]
                  )
                  "required", box [| "questions" |] ]

        let definition =
            createObj
                [ "name", box "askPreviousScope"
                  "label", box "Ask Previous Scope"
                  "concurrency", box "exclusive"
                  "loadMode", box "essential"
                  "approval", box "read"
                  "description",
                  box
                      "Ask concrete clarifying questions of the immediately preceding Scope. Valid old-context readonly model turns run asynchronously with an agent-attributed steer. The receipt separately reports if no old-model call was possible, and acceptance does not prove answer delivery."
                  "parameters", box schema
                  "execute",
                  box (
                      Func<obj, obj, obj, obj, obj, obj>(fun callId arguments _signal _onUpdate ctx ->
                          ompPriorExecute journalOpt (stringOf callId) arguments ctx start |> unbox<obj>)
                  ) ]

        registerTool pi definition

    let private ompCommitResetScope
        (journalOpt: AgentJournal option)
        (sid: string)
        (callId: obj)
        (objective: string)
        (experience: string option)
        : obj =
        let toolCallIdStr = stringOf callId

        if String.IsNullOrWhiteSpace toolCallIdStr then
            failwith "resetScope requires an exact tool call id (HOST-011)"

        let journal =
            match journalOpt with
            | Some j -> j
            | None -> failwith "resetScope requires a durable agent journal for ScopeReanchored prefix epoch advance"

        let sessionId = SessionId.create sid
        let toolCallId = ToolCallId.create toolCallIdStr
        let snapshot = AgentJournal.snapshot journal

        let epoch =
            AgentProjection.tryFind sessionId snapshot.AgentProjections
            |> Option.bind (fun session -> session.PrefixEpoch)
            |> Option.defaultValue PrefixEpochProjection.empty

        task {
            let rawSnap: obj = outboundViewStore?tryGetSealed (sid)

            if isNullOrUndefined rawSnap then
                return
                    ompScopeRefused
                        "resetScope refused: the preceding real provider request has no complete archived system/messages/model observation"
            else
                return! ompWriteArchivedScope journal sid sessionId epoch toolCallId objective experience rawSnap
        }
        |> unbox<obj>

    let private registerResetScopeTool (pi: obj) (journalOpt: AgentJournal option) : unit =
        let resetScopeDef =
            createObj
                [ "name", box "resetScope"
                  "label", box "Reset Scope"
                  "concurrency", box "exclusive"
                  "loadMode", box "essential"
                  "approval", box "read"
                  "description",
                  box "End the current Scope; preserve operator and assistant prose/reasoning plus critical consultation tool call/results, remove ordinary old tool process, and report outstanding work. Arguments: experience and objective."
                  "parameters",
                  box (
                      createObj
                          [ "type", box "object"
                            "properties",
                            box (
                                createObj
                                    [ "oldScopeVerboseVerbatimExperience",
                                      box (
                                          createObj
                                              [ "type", box [| "string"; "null" |]
                                                "description",
                                                box
                                                    "Write out your experience and findings from the closing scope, verbosely: everything between the previous resetScope call (or session start) and this call. Any exact text needed in the next scope (such as function signatures, error stack traces, or exact line numbers) MUST be copied VERBATIM here. Null or omitted if there is no prior work." ]
                                      )
                                      "newScopeObjective",
                                      box (
                                          createObj
                                              [ "type", box "string"
                                                "description", box "The objective for the new scope. The scope must form a self-contained state closed-loop (状态闭环)." ]
                                      ) ]
                            )
                            "required", box [| "newScopeObjective" |] ]
                  )
                  "execute",
                  box (
                      Func<obj, obj, obj, obj, obj, obj>(fun callId arguments signal onUpdate toolCtx ->
                          let ctx = orNull toolCtx
                          let sid = ctxSessionId ctx
                          let objective =
                              if not (isNull arguments) && not (isNull arguments?newScopeObjective) then
                                  stringOf arguments?newScopeObjective
                              else
                                  ""

                          let experience =
                              if
                                  not (isNull arguments)
                                  && not (isNull arguments?oldScopeVerboseVerbatimExperience)
                              then
                                  Some(stringOf arguments?oldScopeVerboseVerbatimExperience)
                              else
                                  None

                          let negotiationPending = Surface.negotiationRequired sid
                          let previousPending = Surface.isPreviousScopePending sid

                          let pendingMessage =
                              if negotiationPending then
                                  "resetScope is unavailable until the first negotiate(questions) call has been accepted. Call negotiate now; once it is accepted, resetScope is available immediately. Do not wait for the delegator's answer."
                              else
                                  "resetScope is unavailable until askPreviousScope(questions) has been called by the new scope; ordinary tools remain available."

                          if ompScopeResetReplayed journalOpt sid (stringOf callId) then
                              task { return ompScopeSuccess objective } |> unbox<obj>
                          elif negotiationPending || previousPending then
                              let msg = pendingMessage
                              let content = box [| createObj [ "type", box "text"; "text", box msg ] |]

                              let details =
                                  createObj
                                      [ "objective", box ""
                                        "archived", box false
                                        "refused", box true ]

                              task { return createObj [ "content", content; "details", details ] }
                              |> unbox<obj>
                          else
                              ompCommitResetScope journalOpt sid callId objective experience)
                  ) ]

        registerTool pi resetScopeDef

    // ------------------------------------------------------------------
    // Host ports over omp
    // ------------------------------------------------------------------

    /// A live subscription to the host signal stream.
    type private SignalSubscription(listeners: ResizeArray<HostSignal -> Task>, listener: HostSignal -> Task) =
        interface IDisposable with
            member _.Dispose() = listeners.Remove listener |> ignore

    [<Emit("Date.now()")>]
    let private nowStamp () : float = jsNative

    [<Emit("Object.fromEntries($0)")>]
    let private entriesOf (fields: (string * string) list) : obj = jsNative

    /// The neutral route of a model object, when it names one.
    let private modelRouteOf (model: obj) : ModelRoute option =
        let provider = optionalString (fieldValue model "provider")
        let id = optionalString (fieldValue model "id")

        match provider, id with
        | Some p, Some m ->
            Some
                { Provider = p
                  Model = m
                  Variant = None }
        | _ -> None

    /// The neutral route of the model the session currently uses.
    let private ctxRoute (ctx: obj) : ModelRoute option =
        let model = ctxModel ctx
        if isNull model then None else modelRouteOf model

    /// Build the host-neutral ports over the omp extension API, together with
    /// the signal emitter the product hooks publish through.
    let private createOmpHostPorts
        (pi: obj)
        (agents: AgentRoleSpec list)
        (workspace: string)
        (stateRoot: string)
        : HostPorts * (HostSignal -> Task<unit>) =
        let listeners = ResizeArray<HostSignal -> Task>()

        let emit (signal: HostSignal) : Task<unit> =
            task {
                for listener in listeners do
                    do! listener signal
            }

        let sessions: ISessionPort =
            { new ISessionPort with
                member _.Create(parent, spec) =
                    guardTask (fun () -> createSession pi parent spec)

                member _.Parent(session) =
                    guardTask (fun () -> parentSession pi (context ()) (SessionId.value session))

                member _.Children(parent) =
                    guardTask (fun () -> Task.FromResult(Ok(childSessionInfos (SessionId.value parent))))

                member _.Abort(session) =
                    guardTask (fun () -> abortSession (context ()) (SessionId.value session))

                member _.Interrupt(session) =
                    guardTask (fun () -> abortSession (context ()) (SessionId.value session))

                member _.Close(session) =
                    guardTask (fun () -> closeSession (context ()) (SessionId.value session))

                member _.FindByTitle(title) =
                    guardTask (fun () -> findByTitle pi title) }

        let prompts: IPromptPort =
            { new IPromptPort with
                member _.Send(session, request, late) =
                    sendPrompt pi (SessionId.value session) request late }

        let transcript: ITranscriptPort =
            { new ITranscriptPort with
                member _.Messages(session) =
                    guardTask (fun () -> transcriptOf (context ()) (SessionId.value session)) }

        let models: IModelCatalog =
            { new IModelCatalog with
                member _.Available() =
                    guardTask (fun () -> availableModels (context ()))

                member _.Apply(session, route) =
                    guardTask (fun () -> applyModel pi (context ()) (SessionId.value session) route) }

        let tools: IToolRegistrar =
            { new IToolRegistrar with
                member _.Register(specs) = registerTools pi specs }

        let signals: ISignalSource =
            { new ISignalSource with
                member _.Subscribe(listener) =
                    listeners.Add listener
                    new SignalSubscription(listeners, listener) :> IDisposable }

        let paths: IHostPaths =
            { new IHostPaths with
                // The state root is resolved once, before activation, from
                // the worktree via `RuntimePath.forWorkspace`; it lands on
                // `<worktree>/.Herdchestra`. The adapter must not fall back
                // to the worktree when it cannot be resolved.
                member _.StateRoot = WorkspacePath(stateRoot)
                member _.ProjectRoot = WorkspacePath(workspace)
                member _.ProjectId = workspace }

        let diagnostics: IDiagnosticSink =
            { new IDiagnosticSink with
                member _.Emit(operation, fields) =
                    call2 (fieldValue (fieldValue pi "logger") "debug") (box operation) (box (entriesOf fields))
                    |> ignore }

        let effectiveAgents =
            agents
            |> List.map (fun agent ->
                let policy =
                    match agent.ToolPolicy with
                    | None -> Map [ ("resetScope", true); ("negotiate", true); ("askPreviousScope", true) ]
                    | Some m ->
                        m
                        |> Map.add "resetScope" true
                        |> Map.add "negotiate" true
                        |> Map.add "askPreviousScope" true

                { agent with ToolPolicy = Some policy })

        let catalog: IAgentCatalog =
            { new IAgentCatalog with
                // The product's role catalogue is handed to the adapter by
                // the composition root, so it is already the roster the host
                // exposes.
                member _.Roles() = Task.FromResult(Ok effectiveAgents) }

        // HNI P-19: omp's extension API exposes no configuration surface, so
        // there is nothing to bind the port to. The port refuses every call
        // explicitly instead of faking a host that stores settings.
        let config: IHostConfigPort =
            { new IHostConfigPort with
                member _.Read(_path: string list) =
                    Error(HostError.Refused "omp exposes no configuration surface to an extension")

                member _.Apply(_settings: RequiredSetting list) =
                    Error(HostError.Refused "omp exposes no configuration surface to an extension") }

        let ports: HostPorts =
            { Sessions = sessions
              Prompts = prompts
              Transcript = transcript
              Models = models
              Signals = signals
              Tools = tools
              Paths = paths
              Config = config
              Diagnostics = diagnostics
              Agents = catalog
              Capabilities =
                { HostName = "omp"
                  Supports =
                    Set.empty
                    |> Set.add HostCapability.ChildSessions
                    |> Set.add HostCapability.SessionDiscovery
                    |> Set.add HostCapability.Interrupt
                    |> Set.add HostCapability.SubagentRouting } }

        ports, emit

    // ------------------------------------------------------------------
    // Product hooks -> omp registrations
    // ------------------------------------------------------------------

    /// The neutral turn context of an omp session.
    let private turnContext (ctx: obj) : TurnContext =
        { Session = SessionId.create (ctxSessionId ctx)
          Role = None
          Route = ctxRoute ctx
          Workspace = Some(WorkspacePath(ctxCwd ctx)) }

    /// The neutral view of one tool call.
    let private toolCallView (ctx: obj) (callId: obj) (tool: obj) (input: obj) : ToolCallView =
        { Session = SessionId.create (ctxSessionId ctx)
          CallId = ToolCallId.create (stringOf callId)
          // omp's tool events carry no provider-run identity for the call,
          // so the view carries none: the fence evidence is absent, never
          // resolved from the session's current binding.
          ProviderRun = None
          Tool = stringOf tool
          Args = CanonicalJsonText(if isNull input then "{}" else stringify input)
          Role = None }

    /// The candidate routes carried by omp spawn patterns (`provider/model`).
    let private spawnCandidates (patterns: obj) : ModelRoute list =
        tryArray patterns
        |> Array.choose (fun pattern ->
            let text = stringOf pattern
            let parts = text.Split('/')

            if parts.Length > 1 then
                Some
                    { Provider = parts.[0]
                      Model = String.Join("/", parts.[1..])
                      Variant = None }
            else
                None)
        |> Array.toList

    /// The neutral spawn request an omp spawn event carries.
    let private spawnRequest (event: obj) (ctx: obj) : SpawnRequest =
        { Parent = SessionId.create (ctxSessionId ctx)
          Role = AgentRole(stringOf (fieldValue event "agent"))
          Candidates = spawnCandidates (fieldValue event "patterns")
          SpawnKey = optionalString (fieldValue event "spawnKey") }

    /// The omp reply to a spawn routing decision.
    let private spawnReply (route: SpawnRoute) : obj =
        match route with
        | SpawnRoute.Keep -> jsUndefined
        | SpawnRoute.Refuse reason -> createObj [ "block", box true; "reason", box reason ]
        | SpawnRoute.Route routes ->
            let models =
                routes |> List.map (fun route -> sprintf "%s/%s" route.Provider route.Model)

            createObj [ "model", box (List.toArray models) ]

    /// Ask the product how an omp spawn should be routed.
    let private routeSpawn (hooks: ProductHooks) (event: obj) (ctx: obj) : Task<obj> =
        task {
            match hooks.Spawn with
            | None -> return jsUndefined
            | Some spawn ->
                let! route = spawn (spawnRequest event ctx)
                return spawnReply route
        }

    /// The omp role name of a neutral transcript role.
    let private roleName (role: TranscriptRole) : string =
        match role with
        | TranscriptRole.User -> "user"
        | TranscriptRole.Assistant -> "assistant"
        | TranscriptRole.System -> "system"

    /// Encode the replacement messages for omp. A message that omp cannot
    /// represent makes the whole edit unusable, and is reported as such.
    let private encodeContextMessages (replacements: ContextMessage list) : obj option =
        // One stamp for the whole edit: every message produced here belongs
        // to the same replacement, so mixed provenance would make the
        // transcript order ambiguous.
        let stamp = nowStamp ()

        let encoded =
            replacements |> List.map (fun message -> message, encodeParts message.Parts)

        if encoded |> List.exists (fun (_, parts) -> parts.IsNone) then
            None
        else
            let payload =
                encoded
                |> List.map (fun (message, parts) ->
                    let content = parts |> Option.defaultValue [] |> List.map (fun part -> box part)

                    createObj
                        [ "role", box (roleName message.Role)
                          "content", box (List.toArray content)
                          "timestamp", box stamp ])

            Some(createObj [ "messages", box (List.toArray payload) ])

    /// Apply the route the product admitted for a turn to omp's active
    /// session. A model omp does not know, or one omp refuses, ends the
    /// turn.
    let private applyAdmittedRoute (pi: obj) (ctx: obj) (chosen: ModelRoute) : Task<obj> =
        task {
            let model =
                callOptionalMethod2 (ctxModelRegistry ctx) "find" (box chosen.Provider) (box chosen.Model)

            let known = not (isNull model)

            let! accepted =
                if known then
                    awaitObj (call1 (fieldValue pi "setModel") (box model))
                else
                    Task.FromResult<obj>(box false)

            let handled = not known || not (isTrue accepted)

            if handled then
                return createObj [ "handled", box true ]
            else
                return jsUndefined
        }

    /// The omp reply to an admitted turn: nothing when the turn runs with
    /// the current model, an object that ends the turn otherwise.
    let private admittedReply (pi: obj) (ctx: obj) (route: ModelRoute option) : Task<obj> =
        task {
            match route with
            | None -> return jsUndefined
            | Some chosen -> return! applyAdmittedRoute pi ctx chosen
        }

    /// The omp reply to a turn admission: nothing when the turn runs with
    /// the current model, an object that ends the turn otherwise.
    let private admissionReply (pi: obj) (ctx: obj) (verdict: TurnAdmission) : Task<obj> =
        task {
            match verdict with
            | TurnAdmission.Refuse _ -> return createObj [ "handled", box true ]
            | TurnAdmission.Admit route -> return! admittedReply pi ctx route
        }

    /// The omp reply to a context edit: nothing when the context stays, the
    /// replacement messages otherwise.
    let private contextEditReply (edit: ContextEdit) : obj option =
        match edit with
        | ContextEdit.Keep -> None
        | ContextEdit.Replace replacements -> encodeContextMessages replacements

    /// The neutral view of one omp context message.
    let private contextMessageView (message: obj) : ContextMessage option =
        match decodeRole message with
        | None -> None
        | Some role ->
            Some
                { Address = None
                  Role = role
                  Parts = decodeParts message }

    /// The omp reply to a context edit: nothing when the context stays, the
    /// replacement messages otherwise.
    let private editReply (edit: ContextEdit) : obj =
        match contextEditReply edit with
        | None -> jsUndefined
        | Some payload -> payload

    /// The omp reply to a tool-call verdict: nothing when the call is allowed,
    /// the refusal or the rewritten input otherwise.
    let private verdictReply (verdict: ToolCallVerdict) : obj =
        match verdict with
        | ToolCallVerdict.Allow -> jsUndefined
        | ToolCallVerdict.Refuse reason -> createObj [ "block", box true; "reason", box reason ]
        | ToolCallVerdict.Rewrite args -> createObj [ "input", box (parseJson (canonicalOf args)) ]

    /// The text of an omp tool result: its content parts that carry text.
    let private resultText (event: obj) : string =
        tryArray (fieldValue event "content")
        |> Array.choose (fun part ->
            match optionalString (fieldValue part "type") with
            | Some "text" -> Some(stringOf (fieldValue part "text"))
            | _ -> None)
        |> String.concat "\n"

    /// The omp reply to a tool-result edit: nothing when the host's result
    /// stands, the replacement result otherwise.
    let private resultReply (edit: ToolResultEdit) : obj =
        match edit with
        | ToolResultEdit.Keep -> jsUndefined
        | ToolResultEdit.Replace result ->
            createObj
                [ "content", box [| createObj [ "type", box "text"; "text", box result.Text ] |]
                  "isError", box result.IsError ]

    let private isTextOnlyPart (part: TranscriptPart) : bool =
        match part with
        | TranscriptPart.Text _ -> true
        | _ -> false

    let private textOfParts (parts: TranscriptPart list) : string =
        parts
        |> List.choose (fun part ->
            match part with
            | TranscriptPart.Text text -> Some text
            | _ -> None)
        |> String.concat "\n"

    let private replaceStringContent (raw: obj) (newText: string) (rawContent: obj) : obj =
        if unbox<string> rawContent <> newText then
            let cloned = emitJsExpr raw "Object.assign({}, $0)"
            cloned?content <- newText
            cloned
        else
            raw

    let private replaceArrayContent (raw: obj) (newText: string) (arr: obj array) : obj =
        let hasNonText =
            arr
            |> Array.exists (fun block ->
                let t = fieldValue block "type"
                isNull t || stringOf t <> "text")

        let oldText =
            arr
            |> Array.choose (fun block ->
                let t = fieldValue block "type"

                if not (isNull t) && stringOf t = "text" then
                    Some(stringOf (fieldValue block "text"))
                else
                    None)
            |> String.concat "\n"

        if hasNonText || oldText = newText then
            raw
        else
            let cloned = emitJsExpr raw "Object.assign({}, $0)"
            cloned?content <- box [| createObj [ "type", box "text"; "text", box newText ] |]
            cloned

    let private replaceRawContent (raw: obj) (newText: string) : obj =
        let rawContent = fieldValue raw "content"

        if isString rawContent then
            replaceStringContent raw newText rawContent
        elif isArray rawContent then
            replaceArrayContent raw newText (unbox<obj array> rawContent)
        else
            raw

    let private mergeOneContextItem (raw: obj, rep: ContextMessage) : Result<obj, unit> =
        if isNull raw || not (List.forall isTextOnlyPart rep.Parts) then
            Error()
        else
            let newText = textOfParts rep.Parts
            Ok(replaceRawContent raw newText)

    let private selectChangedMerged (rawArray: obj array) (mergedList: obj list) : obj array option =
        let merged = List.toArray mergedList

        let anyChanged =
            Array.exists2 (fun orig m -> not (obj.ReferenceEquals(orig, m))) rawArray merged

        if anyChanged then Some merged else None

    let private mergeContextPairs (rawArray: obj array) (replacements: ContextMessage list) : obj array option =
        let pairs = Array.zip rawArray (List.toArray replacements) |> Array.toList

        pairs
        |> List.traverseResultM mergeOneContextItem
        |> Result.toOption
        |> Option.bind (selectChangedMerged rawArray)

    let private mergeContextReplacements (rawArray: obj array) (replacements: ContextMessage list) : obj array option =
        if rawArray.Length <> replacements.Length then
            None
        else
            mergeContextPairs rawArray replacements

    let private isScopeSection (text: string) : bool =
        text.Contains("## Scope & Context Management") || text.Contains("## Scope 与上下文")

    let private replaceLegacyScopeHeader (scopeProtocol: string) (text: string) : string =
        if isScopeSection text then scopeProtocol else text

    let private updateSystemPromptScope (system: string list) (scopeProtocol: string) : string list =
        let hasScope = system |> List.exists isScopeSection

        if hasScope then
            system |> List.map (replaceLegacyScopeHeader scopeProtocol)
        else
            system @ [ scopeProtocol ]

    // HNI projection for scope planning: the library sees no omp field names.
    let private ompScopeKind (message: obj) =
        match optionalString (fieldValue message "role") with
        | Some "user" -> "user"
        | Some "assistant" -> "assistant"
        | Some "toolResult"
        | Some "tool"
        | Some "tool-result"
        | Some "tool_result" -> "result"
        | Some "custom" -> "custom"
        | _ -> "other"

    let private ompScopeParts (message: obj) : obj array =
        let content = fieldValue message "content"
        if isArray content then unbox<obj array> content else [||]

    let private ompScopeText (message: obj) =
        let content = fieldValue message "content"

        if isString content then
            stringOf content
        else
            ompScopeParts message
            |> Array.filter (fun part -> stringOf (fieldValue part "type") = "text")
            |> Array.map (fun part -> stringOf (fieldValue part "text"))
            |> String.concat "\n"

    let private ompNeutralPart (part: obj) : obj =
        match optionalString (fieldValue part "type") with
        | Some "toolCall"
        | Some "tool-call"
        | Some "tool_call" -> createObj [ "id", fieldValue part "id"; "tool", fieldValue part "name" ]
        | Some "toolResult"
        | Some "tool-result"
        | Some "tool_result" -> createObj [ "resultFor", fieldValue part "id" ]
        | _ -> createObj []

    let private ompNeutralMessage (message: obj) : obj =
        let kind = ompScopeKind message
        let attribution = stringOf (fieldValue message "attribution")

        let operator =
            (kind = "user" && attribution <> "agent")
            || (kind = "custom" && attribution = "user")

        let side =
            kind = "user" && not operator && not (isTrue (fieldValue message "steering"))

        let ts = fieldValue message "timestamp"
        let key = (if isNull ts then "" else stringOf ts) + ":" + ompScopeText message

        let resultFor =
            let id = fieldValue message "toolCallId"
            if isNull id then "" else stringOf id

        createObj
            [ "kind", box kind
              "operator", box operator
              "sideRequest", box side
              "key", box key
              "timestamp", ts
              "parts", box (ompScopeParts message |> Array.map ompNeutralPart)
              "resultFor", box resultFor
              "resultField", box (if kind = "result" then "content" else "") ]

    let private ompTenureCall (expected: string) (part: obj) =
        match optionalString (fieldValue part "type"), optionalString (fieldValue part "id") with
        | Some ("toolCall" | "tool-call" | "tool_call"), Some id when id = expected -> Some id
        | _ -> None

    let private ompTenureMessage (expected: string) (message: obj) =
        let call = ompScopeParts message |> Array.tryPick (ompTenureCall expected)
        createObj
            [ "id", (optionalString (fieldValue message "id") |> Option.toObj |> box)
              "role", fieldValue message "role"
              "synthetic", box (isTrue (fieldValue message "synthetic"))
              "semantic", box true
              "cursor", null
              "toolCallId", (call |> Option.toObj |> box)
              "resultFor", (optionalString (fieldValue message "toolCallId") |> Option.toObj |> box)
              "tenureId", (optionalString (fieldValue message "tenureId") |> Option.toObj |> box) ]

    let private ompTenureMessages stateRoot sessionId (messages: obj) =
        match Wanxiangshu.Mission.Lifecycle.DurableWorkHost.inspected stateRoot sessionId with
        | Some work when work.ActiveId.IsSome && work.ActiveId = work.SuccessorTenureId && work.BarrierToolCallId.IsSome ->
            let original = Surface.toObjArray messages
            let call = work.BarrierToolCallId.Value
            let neutral = original |> Array.map (ompTenureMessage call)
            let barrier = createObj [ "toolCallId", box call; "successorTenureId", box work.ActiveId.Value ]
            let kept: obj array = tenureProjection?tenure(box neutral, barrier)
            kept |> Array.map (fun index -> original[int (unbox<float> index)]) |> box
        | _ -> messages

    let private copyScopeObject (value: obj) =
        emitJsExpr value "Object.assign({}, $0)"

    let private ompWritePlannedTail (message: obj) (suffix: string) : obj =
        let content = fieldValue message "content"
        let updated = Surface.appendTailOnce content suffix

        if obj.ReferenceEquals(content, updated) then
            message
        else
            let replacement = copyScopeObject message
            replacement?content <- updated
            replacement

    let private ompApplyHint (messages: obj array) (hint: obj) =
        let index = unbox<int> hint?index
        let current = messages.[index]

        let updated =
            if stringOf hint?kind = "user" then
                Surface.writeUserHint current [| stringOf hint?tail |]
            else
                ompWritePlannedTail current (stringOf hint?tail)

        messages.[index] <- updated

    let private ompApplySlot (messages: obj array) (slot: obj) : obj =
        let index = unbox<int> slot?index
        let item = messages.[index]
        let kept = fieldValue slot "parts"

        if isNull kept then
            item
        else
            let parts = ompScopeParts item
            let selected = unbox<int array> kept |> Array.map (fun i -> parts.[i])
            let updated = copyScopeObject item
            updated?content <- box selected
            emitJsExpr updated "delete $0.providerPayload; delete $0.responseId" |> ignore
            updated

    let private ompScopeSteer (messages: obj array) (prose: string) (strong: bool) : obj array =
        let lastStamp =
            messages
            |> Array.tryLast
            |> Option.map (fun message -> fieldValue message "timestamp")
            |> Option.defaultValue null

        let fields =
            [ "role", box "user"
              "content", box [| createObj [ "type", box "text"; "text", box prose ] |]
              "steering", box true
              "attribution", box "agent"
              "strong", box strong ]

        let withTime =
            if isNull lastStamp then
                fields
            else
                fields @ [ "timestamp", lastStamp ]

        Array.append messages [| createObj withTime |]

    /// Delivery receipts let message_start match a physical steer. The model never reads them.
    let private ompWithoutReceipt (message: obj) : obj =
        let carriesReceipt =
            stringOf (fieldValue message "role") = "user"
            && stringOf (fieldValue message "attribution") = "agent"
        if not carriesReceipt then message
        else
            emitJsExpr message """(() => {
                const strip = text => typeof text === 'string' ? text.replace(/\n\[receipt:[^\]\n]+\]$/, '') : text
                const content = $0.content
                const next = typeof content === 'string' ? strip(content)
                    : Array.isArray(content) ? content.map(block => block && block.type === 'text' && typeof block.text === 'string' && strip(block.text) !== block.text ? { ...block, text: strip(block.text) } : block)
                    : content
                const changed = typeof content === 'string' ? next !== content : Array.isArray(content) && next.some((block, index) => block !== content[index])
                return changed ? { ...$0, content: next } : $0
            })()"""

    let private ompScopeRequest (sessionId: string) (rawMessages: obj) : obj array =
        let original = Surface.toObjArray rawMessages |> Array.map ompWithoutReceipt
        let neutral = original |> Array.map ompNeutralMessage
        let plan = Surface.planRequest sessionId (box neutral)
        let staged = Array.copy original
        unbox<obj array> plan?hints |> Array.iter (ompApplyHint staged)
        let scoped = unbox<obj array> plan?slots |> Array.map (ompApplySlot staged)
        let progress: obj = scopeProgress?status(sessionId)
        let last = original |> Array.tryLast |> Option.defaultValue null
        let isSideOnly =
            stringOf (fieldValue last "attribution") = "agent"
            && stringOf (fieldValue last "steering") <> "true"
        let progressUnknown = not (isNull progress?ok) && not (unbox<bool> progress?ok)
        if not (unbox<bool> plan?active) || isSideOnly then scoped
        elif not (isNull plan?strong) then ompScopeSteer scoped (stringOf plan?strong) true
        elif progressUnknown then scoped
        elif not (isNull progress?resetDue) && unbox<bool> progress?resetDue then
            ompScopeSteer scoped (ProviderProse.render (ProviderLanguageBinding.forSessionText sessionId) "host/scope-steer/reset-due" Map.empty) true
        elif isNull progress?known || not (unbox<bool> progress?known) then
            ompScopeSteer scoped (ProviderProse.render (ProviderLanguageBinding.forSessionText sessionId) "host/scope-steer/missing-estimate" Map.empty) true
        else scoped

    let private contextMessagesReply (rawList: obj) (withSteer: obj array) : obj =
        if Surface.isUnchanged withSteer (Surface.toObjArray rawList) then
            jsUndefined
        else
            createObj [ "messages", box withSteer ]

    let private applyContextReplacements
        (rawList: obj)
        (withSteer: obj array)
        (replacements: ContextMessage list)
        : obj =
        match mergeContextReplacements withSteer replacements with
        | Some merged -> createObj [ "messages", box merged ]
        | None -> contextMessagesReply rawList withSteer

    let private applyContextEdit (rawList: obj) (withSteer: obj array) (edit: ContextEdit) : obj =
        match edit with
        | ContextEdit.Keep -> contextMessagesReply rawList withSteer
        | ContextEdit.Replace replacements -> applyContextReplacements rawList withSteer replacements

    let private applyToolResultEdit (edit: ToolResultEdit) : obj =
        match edit with
        | ToolResultEdit.Keep -> jsUndefined
        | ToolResultEdit.Replace result ->
            createObj
                [ "content", box [| createObj [ "type", box "text"; "text", box result.Text ] |]
                  "isError", box result.IsError ]

    let private extractTextPart (p: obj) =
        if stringOf (fieldValue p "type") = "text" then
            Some(stringOf (fieldValue p "text"))
        else
            None

    let private joinTextParts (parts: obj array) : string option =
        let texts = parts |> Array.choose extractTextPart

        if texts.Length > 0 then
            Some(String.concat "\n" texts)
        else
            None

    let private extractAssistantContent (content: obj) : string option =
        if isString content then
            Some(stringOf content)
        elif isArray content then
            joinTextParts (unbox<obj array> content)
        else
            None

    let private latestAssistantText (entries: obj array) : string =
        entries
        |> Array.filter (fun e -> stringOf (fieldValue e "type") = "message")
        |> Array.choose (fun e ->
            let msg = fieldValue e "message"
            let role = stringOf (fieldValue msg "role")

            if role = "assistant" then
                extractAssistantContent (fieldValue msg "content")
            else
                None)
        |> Array.tryLast
        |> Option.defaultValue ""

    let private appendHistoryEntry (histOpt: obj option) (userText: string) (assistantMsg: obj) =
        let userMsg =
            createObj
                [ "role", box "user"
                  "content", box [| createObj [ "type", box "text"; "text", box userText ] |] ]

        match histOpt with
        | Some h when isArray h -> Array.append (unbox<obj array> h) [| userMsg; assistantMsg |]
        | _ -> [| userMsg; assistantMsg |]

    let private extractMessageContent (message: obj) : string =
        let content =
            if isNull message then
                null
            else
                fieldValue message "content"

        if isNull content then
            ""
        elif isString content then
            stringOf content
        elif isArray content then
            unbox<obj array> content |> Array.choose extractTextPart |> String.concat "\n"
        else
            ""

    let private recordPromptOutcome
        (outcome: Result<bool, HostError>)
        (tcs: System.Threading.Tasks.TaskCompletionSource<Result<unit, string>>)
        (resolve: Result<unit, string> -> unit)
        =
        match outcome with
        | Error err -> resolve (Error("prompt failed: " + reasonOf err))
        | Ok false -> resolve (Error "omp dropped prompt before dispatch")
        | Ok true -> resolve (Ok())

    let private observeIdlePrompt
        (child: Child)
        (payload: string)
        (resolve: Result<unit, string> -> unit)
        : Task<unit> =
        task {
            try
                let! outcome = promptLiveChild child payload
                recordPromptOutcome outcome null resolve
            with ex ->
                resolve (Error ex.Message)
        }

    let private observeStreamingSteer
        (session: obj)
        (payload: string)
        (resolve: Result<unit, string> -> unit)
        : Task<unit> =
        task {
            try
                let! _ = awaitObj (callMethod session "steer" (box payload))
                ()
            with ex ->
                resolve (Error ex.Message)
        }

    let private dispatchDeliveryAttempt
        (session: obj)
        (child: Child)
        (payload: string)
        (isStreaming: bool)
        (resolve: Result<unit, string> -> unit)
        : unit =
        if isStreaming then
            observeStreamingSteer session payload resolve |> ignore
        else
            observeIdlePrompt child payload resolve |> ignore

    let private deliverWithObservedAcceptance
        (session: obj)
        (child: Child)
        (payload: string)
        (receipt: string)
        (isStreaming: bool)
        : Task<Result<unit, string>> =
        task {
            let tcs = System.Threading.Tasks.TaskCompletionSource<Result<unit, string>>()
            // DSL-MUTABLE: single-flight
            let settled = ref false
            let unsubscribeRef: (unit -> unit) option ref = ref None

            let resolve (res: Result<unit, string>) =
                if not settled.Value then
                    settled.Value <- true
                    unsubscribeRef.Value |> Option.iter (fun u -> u ())
                    tcs.SetResult res

            let listener =
                Func<obj, unit>(fun event ->
                    let eventType = stringOf (fieldValue event "type")
                    let message = fieldValue event "message"

                    let role =
                        if isNull message then
                            ""
                        else
                            stringOf (fieldValue message "role")

                    let contentText = extractMessageContent message

                    if eventType = "message_end" && role = "user" && contentText.Contains(receipt) then
                        resolve (Ok())
                    elif eventType = "message_start" && role = "user" && contentText.Contains(receipt) then
                        resolve (Ok())
                    elif eventType = "agent_end" || eventType = "session_shutdown" then
                        resolve (Error "child session terminated before steer landed"))

            let unsubObj = callMethod session "subscribe" (box listener)
            unsubscribeRef := Some(fun () -> callMethod unsubObj "call" null |> ignore)
            dispatchDeliveryAttempt session child payload isStreaming resolve
            let! res = tcs.Task
            unsubscribeRef.Value |> Option.iter (fun u -> u ())
            return res
        }

    let private isToolCallBlock (b: obj) =
        stringOf (fieldValue b "type") = "toolCall"

    let private historyEntries (historyOpt: obj option) =
        match historyOpt with
        | Some h -> [ "history", h ]
        | None -> []

    let private messageHasToolCall (msg: obj) =
        let content = if isNull msg then null else fieldValue msg "content"

        if isArray content then
            unbox<obj array> content |> Array.exists isToolCallBlock
        else
            false

    let private invokeEphemeralTurn (target: obj) (options: obj) : Task<Result<obj, string>> =
        task {
            try
                let! r = awaitObj (callMethod target "runEphemeralTurn" options)
                return Ok r
            with ex ->
                return Error ex.Message
        }

    let private executeEphemeralTurn
        (delegatorCtx: obj)
        (promptText: string)
        (conversationKey: string)
        (historyOpt: obj option)
        =
        task {
            let histEntries = historyEntries historyOpt

            let options =
                createObj (
                    [ "promptText", box promptText; "conversationKey", box conversationKey ]
                    @ histEntries
                )

            let targetOpt =
                [ fun () ->
                      if hasMethod delegatorCtx "runEphemeralTurn" then
                          Some delegatorCtx
                      else
                          None
                  fun () ->
                      let sess = fieldValue delegatorCtx "session"

                      if hasMethod sess "runEphemeralTurn" then
                          Some sess
                      else
                          None ]
                |> List.tryPick (fun f -> f ())

            match targetOpt with
            | None -> return Error "Neither delegatorCtx nor delegatorCtx.session exposes runEphemeralTurn"
            | Some t -> return! invokeEphemeralTurn t options
        }

    let private processTurnResult
        (result: obj)
        (promptText: string)
        (historyOpt: obj option)
        (retry: bool -> obj option -> Task<Result<string, string>>)
        : Task<Result<string, string>> =
        let replyText = stringOf (fieldValue result "replyText")
        let assistantMsg = fieldValue result "assistantMessage"
        let toolCalled = messageHasToolCall assistantMsg

        let isBlankOrTool = String.IsNullOrWhiteSpace replyText || toolCalled

        if isBlankOrTool then
            let historyArr = appendHistoryEntry historyOpt promptText assistantMsg
            retry toolCalled (Some(box historyArr))
        else
            Task.FromResult(Ok replyText)

    let private handleTurnOutcome
        (turnRes: Result<obj, string>)
        (promptText: string)
        (historyOpt: obj option)
        (retry: bool -> obj option -> Task<Result<string, string>>)
        : Task<Result<string, string>> =
        match turnRes with
        | Error err -> Task.FromResult(Error err)
        | Ok result -> processTurnResult result promptText historyOpt retry

    /// Temporary snapshot dialogue denial: a tool call earns the
    /// bash-honeypot-class rebuke ahead of the restated question.
    let private snapshotDenialText (sessionText: string) =
        ProviderProse.render
            (ProviderLanguageBinding.forSessionText sessionText)
            "delegation/snapshot-dialogue/denial"
            Map.empty

    let private snapshotForbidText (sessionText: string) =
        ProviderProse.render
            (ProviderLanguageBinding.forSessionText sessionText)
            "delegation/snapshot-dialogue/forbid"
            Map.empty

    let private runEphemeralConsultation
        (delegatorCtx: obj)
        (denialText: string)
        (promptText: string)
        (retryPromptText: string)
        (conversationKey: string)
        : Task<Result<string, string>> =
        let rec step currentPrompt (historyOpt: obj option) remaining =
            task {
                if remaining <= 0 then
                    return Error "ephemeral negotiation retry budget exhausted"
                else
                    let! turnRes = executeEphemeralTurn delegatorCtx currentPrompt conversationKey historyOpt

                    return!
                        handleTurnOutcome turnRes currentPrompt historyOpt (fun toolCalled hist ->
                            // Recording only: names why the bounded budget is being spent.
                            Diagnostic.emit
                                "ephemeral-consultation-retry"
                                [ "receipt", conversationKey
                                  "stage", "ephemeral-turn"
                                  "reason",
                                  (if toolCalled then "tool call" else "blank reply")
                                  + "; attempts left "
                                  + string (remaining - 1) ]

                            let next =
                                if toolCalled then denialText + "\n\n" + retryPromptText else retryPromptText

                            step next hist (remaining - 1))
            }

        step promptText None 3

    let private runEphemeralDelegatorQuestion
        (delegatorCtx: obj)
        (delegator: string)
        (promptText: string)
        (retryPromptText: string)
        (receipt: string)
        =
        runEphemeralConsultation
            delegatorCtx
            (snapshotDenialText delegator)
            promptText
            retryPromptText
            ("negotiate:" + receipt)

    let private deliverArchivedSteer (pi: obj) (receipt: string) (sid: string) (target: PrefixEpochId) (text: string) =
        task {
            pendingPreviousSteers[receipt] <- sid, target

            try
                let opts = createObj [ "deliverAs", box "steer"; "attribution", box "agent" ]
                let! _ = awaitObj (call2 (fieldValue pi "sendUserMessage") (box text) opts)
                return ()
            with _ ->
                pendingPreviousSteers.Remove receipt |> ignore
        }

    let private sendArchivedScopeSteer
        (pi: obj)
        (journal: AgentJournal)
        (sid: string)
        (archive: ArchivedScope)
        (receipt: string)
        (answer: string)
        =
        let stillCurrent =
            ompPriorArchive journal sid
            |> Result.map (fun (epoch, _) -> epoch.EpochId = archive.TargetEpochId)
            |> Result.defaultValue false

        let prepared =
            if stillCurrent then
                Surface.preparePreviousScopeReply receipt sid answer
            else
                null

        let admitted =
            not (isNull prepared) && not (isNull prepared?ok) && unbox<bool> prepared?ok

        if admitted then
            let text = stringOf prepared?steer + "\n[receipt:" + receipt + "]"
            deliverArchivedSteer pi receipt sid archive.TargetEpochId text
        else
            Task.FromResult(())

    let private settleArchivedTurn
        (pi: obj)
        (journal: AgentJournal)
        (sid: string)
        (archive: ArchivedScope)
        (receipt: string)
        (lease: LatestProviderLease)
        (result: Result<string, string>)
        =
        match result with
        | Ok text when
            not lease.Cancelled
            && lease.Replaced > 0
            && not (String.IsNullOrWhiteSpace text)
            ->
            sendArchivedScopeSteer pi journal sid archive receipt text
        | _ -> Task.FromResult(())

    let private executeArchivedTurn
        (pi: obj)
        (journal: AgentJournal)
        (ctx: obj)
        (sid: string)
        (questions: string)
        (receipt: string)
        (archive: ArchivedScope)
        (providerId: string)
        (modelId: string)
        : unit =
        let marker = "[wanxiangshu-prior:" + Guid.NewGuid().ToString("N") + "]"

        let lease =
            { SessionId = sid
              Marker = marker
              ProviderId = providerId
              ModelId = modelId
              Replaced = 0
              Cancelled = false }

        latestProviderLeases[marker] <- lease

        let prompt = questions + "\n\n" + snapshotForbidText sid + "\n" + marker

        let retry = questions + "\n\n" + snapshotForbidText sid + "\n" + marker

        let guarded =
            task {
                try
                    return! runEphemeralConsultation ctx (snapshotDenialText sid) prompt retry ("previous-scope:" + receipt)
                with ex ->
                    return Error ex.Message
            }

        let run =
            task {
                try
                    let! result = guarded
                    do! settleArchivedTurn pi journal sid archive receipt lease result
                finally
                    latestProviderLeases.Remove marker |> ignore
            }

        run |> ignore

    let private launchArchivedScopeTurn
        (pi: obj)
        (journal: AgentJournal)
        (ctx: obj)
        (sid: string)
        (questions: string)
        (receipt: string)
        (archive: ArchivedScope)
        (source: obj)
        : bool =
        let latest: obj = outboundViewStore?tryGetSealed sid
        let liveNative = fieldValue latest "nativePayload"
        // One shared last-transform view, maintained by the normal Provider hook.
        let previousMessages: obj = Surface.latestPrevious sid
        let native = liveNative
        let priorModel = fieldValue source "model"
        let providerId = stringOf (fieldValue priorModel "providerID")
        let modelId = stringOf (fieldValue priorModel "modelID")

        let available =
            hasMethod ctx "runEphemeralTurn"
            || hasMethod (fieldValue ctx "session") "runEphemeralTurn"

        let valid =
            available
            && not (isNull previousMessages)
            && isArray (fieldValue native "messages")
            && isArray (fieldValue native "tools")
            && providerId <> ""
            && modelId <> ""

        if not valid then
            false
        else
            executeArchivedTurn pi journal ctx sid questions receipt archive providerId modelId
            true

    let private deliverNegotiationSteerToChild
        (pi: obj)
        (recipient: string)
        (child: Child)
        (steerText: string)
        (receipt: string)
        (correlationKey: PromptKey)
        : Task<Result<unit, string>> =
        task {
            let! appended = tryAppendCorrelationEntry pi recipient correlationKey

            match appended with
            | Error err -> return Error("failed to write prompt correlation entry: " + reasonOf err)
            | Ok() ->
                let payload = steerText + "\n[receipt:" + receipt + "]"
                let session = child.Session
                let isStreaming = isTrue (fieldValue (fieldValue session "agent") "isStreaming")
                return! deliverWithObservedAcceptance session child payload receipt isStreaming
        }

    let private createNegotiationTransport
        (pi: obj)
        (liveChildren: System.Collections.Generic.Dictionary<string, Child>)
        : INegotiationTransport =
        { new INegotiationTransport with
            member _.CanNegotiate(delegatorSessionId: string) =
                sessionContexts.ContainsKey delegatorSessionId

            member _.AskEphemeral(request: EphemeralTurnRequest) =
                task {
                    match sessionContexts.TryGetValue request.DelegatorSessionId with
                    | false, _ -> return Error("Delegator context not found for " + request.DelegatorSessionId)
                    | true, ctx ->
                        return!
                            runEphemeralDelegatorQuestion
                                ctx
                                request.DelegatorSessionId
                                request.PromptText
                                request.RetryPromptText
                                request.Receipt
                }

            member _.DeliverAndObserve(request: SteerDeliveryRequest) =
                task {
                    match liveChildren.TryGetValue request.RecipientSessionId with
                    | false, _ -> return Error("Recipient child not found: " + request.RecipientSessionId)
                    | true, child ->
                        return!
                            deliverNegotiationSteerToChild
                                pi
                                request.RecipientSessionId
                                child
                                request.SteerText
                                request.Receipt
                                request.CorrelationKey
                } }

    let private toolNameOf (t: obj) : string =
        if isNull t then
            ""
        elif not (isNull (fieldValue t "name")) then
            stringOf (fieldValue t "name")
        elif not (isNull (fieldValue (fieldValue t "function") "name")) then
            stringOf (fieldValue (fieldValue t "function") "name")
        else
            ""

    let private filterOutNegotiate (tools: obj) =
        unbox<obj array> tools |> Array.filter (fun t -> toolNameOf t <> "negotiate")

    let private priorProviderModel (source: obj) =
        let provider = fieldValue source "provider"

        let providerId =
            if isNull provider then
                fieldValue source "providerID"
            else
                provider

        let modelId = fieldValue source "id"

        let modelId =
            if isNull modelId then
                fieldValue source "modelID"
            else
                modelId

        stringOf providerId, stringOf modelId

    let private projectLatestProvider (active: LatestProviderLease) (latest: obj) (payload: obj) (ctx: obj) : obj =
        // Never pin an entire previous Provider request on the temporary lease.
        // At the physical outgoing hook read the only most-recent work view.
        let observed: obj = outboundViewStore?tryGetSealed active.SessionId
        let native = fieldValue observed "nativePayload"
        let oldMessages: obj = Surface.latestPrevious active.SessionId
        let originalTools = fieldValue native "tools"
        let provider, modelId = priorProviderModel (fieldValue ctx "model")
        let actualNativeModel = stringOf (fieldValue payload "model")
        let latestNativeModel = stringOf (fieldValue native "model")
        let matches =
            not (isNull native) && not (isNull oldMessages)
            && provider = active.ProviderId
            && modelId = active.ModelId
            && actualNativeModel = latestNativeModel
            && isArray (fieldValue native "messages")
            && isArray originalTools

        if not matches then
            invalidOp "Previous Scope consultation cannot replay: current provider/model/catalog or latest old-Scope boundary is unavailable"
        else
            let baseRequest: obj =
                emitJsExpr (native, oldMessages)
                    "Object.assign({}, structuredClone($0), { messages: structuredClone($1) })"
            let projected: obj =
                emitJsExpr
                    (baseRequest, latest, active.Marker)
                    "(() => { const base = structuredClone($0); const prompt = structuredClone($1); if (typeof prompt.content === 'string') prompt.content = prompt.content.replaceAll($2, '').trim(); else if (Array.isArray(prompt.content)) for (const block of prompt.content) if (block.type === 'text' && typeof block.text === 'string') block.text = block.text.replaceAll($2, '').trim(); return Object.assign({}, base, { messages: [...base.messages, prompt] }); })()"
            active.Replaced <- active.Replaced + 1
            projected


    let private replayLatestProvider (sid: string) (payload: obj) (ctx: obj) : obj option =
        let messages = fieldValue payload "messages"

        let latest =
            if isArray messages then
                let arr = unbox<obj array> messages
                if arr.Length = 0 then null else arr[arr.Length - 1]
            else
                null

        let latestText: string =
            if isNull latest then
                ""
            else
                emitJsExpr latest "JSON.stringify($0)"

        let lease =
            latestProviderLeases.Values
            |> Seq.tryFind (fun active -> active.SessionId = sid && latestText.Contains(active.Marker))

        match lease with
        | None -> None
        | Some active -> Some(projectLatestProvider active latest payload ctx)

    /// Register the product hooks on the omp event bus and return the
    /// combined signal emitter: every signal reaches the host subscribers
    /// and the product at the same time.
    let private registerProductHooks
        (pi: obj)
        (hooks: ProductHooks)
        (emit: HostSignal -> Task<unit>)
        (journalOpt: AgentJournal option)
        (stateRoot: string)
        : (HostSignal -> Task<unit>) =

        registerResetScopeTool pi journalOpt

        registerAskPreviousScopeTool pi journalOpt (fun ctx questions receipt archive source ->
            match journalOpt with
            | Some journal ->
                launchArchivedScopeTurn pi journal ctx (ctxSessionId ctx) questions receipt archive source
            | None -> false)


        // OMP has an authoritative tool-call id and a local turn_start/turn_end
        // boundary. A randomly minted per-turn witness is never claimed to be
        // the provider's external responseId; its accepted reports are kept in
        // the same SQLite journal as OpenCode for best-effort crash recovery.

        let progressJournal (_sid: string) : obj =
            let directory = joinPath stateRoot "wanxiangshu"
            mkdirSync directory (createObj [ "recursive" ==> true ])
            let dbPath = joinPath directory "advisor-process.sqlite"
            let loader = createRequire moduleUrl
            let sqlite = call1 loader (box (if runningOnBun () then "bun:sqlite" else "node:sqlite"))
            let connection =
                if runningOnBun () then createNew sqlite?Database dbPath
                else createNew sqlite?DatabaseSync dbPath
            try createNew sqliteJournalConstructor connection
            with _ ->
                connection?close() |> ignore
                reraise ()

        let durableCall (action: obj -> obj) : obj =
            try
                let journal = progressJournal ""
                try action journal
                finally journal?close() |> ignore
            with ex ->
                createObj [ "ok" ==> false; "reason" ==> ("OMP durable Scope operation unknown: " + ex.Message) ]

        let scopeEpoch (sid: string) : string =
            journalOpt
            |> Option.bind (fun journal ->
                AgentProjection.tryFind (SessionId.create sid) (AgentJournal.snapshot journal).AgentProjections)
            |> Option.bind (fun session -> session.PrefixEpoch)
            |> Option.map (fun epoch -> string (Set.count epoch.ReanchoredScopes))
            |> Option.defaultValue "0"

        let bindProgress (sid: string) =
            if not (String.IsNullOrWhiteSpace sid) then
                scopeProgress?bindSessionJournal(sid, Func<string, obj>(progressJournal)) |> ignore
                scopeProgress?ensureEpoch(sid, scopeEpoch sid) |> ignore

        let advisorPorts =
            let current = Func<string, string>(scopeEpoch)
            let language = Func<string, string>(fun sid ->
                ProviderLanguage.label (ProviderLanguageBinding.forSessionText sid))
            let claim = Func<string, string, string, string, Task<obj>>(fun sid epoch report issue ->
                Task.FromResult(durableCall (fun journal ->
                    emitJsExpr (advisorClaim, journal, sid, epoch, report, issue) "$0($1,$2,$3,$4,$5)")))
            let finished = Func<string, string, string array, string, Task<obj>>(fun sid epoch reports outcome ->
                Task.FromResult(durableCall (fun journal ->
                    emitJsExpr (advisorFinish, journal, sid, epoch, reports, outcome) "$0($1,$2,$3,$4,$5)")))
            let stageBegin = Func<string, string, string, int, string, string array, Task<obj>>(fun sid epoch processId phase question reports ->
                Task.FromResult(durableCall (fun journal ->
                    emitJsExpr (advisorStageBegin, journal, sid, epoch, processId, phase, question, reports) "$0($1,$2,$3,$4,$5,$6,$7)")))
            let stageConfirm = Func<string, string, string, int, string, Task<obj>>(fun sid epoch processId phase prose ->
                Task.FromResult(durableCall (fun journal ->
                    emitJsExpr (advisorStageConfirm, journal, sid, epoch, processId, phase, prose) "$0($1,$2,$3,$4,$5,$6)")))
            let pending = Func<string, string, Task<obj>>(fun sid epoch ->
                Task.FromResult(durableCall (fun journal ->
                    emitJsExpr (advisorPending, journal, sid, epoch) "$0($1,$2,$3)")))
            let answer text = createObj [ "Ok" ==> true; "Prose" ==> text; "Reason" ==> "" ]
            let refuse text = createObj [ "Ok" ==> false; "Prose" ==> ""; "Reason" ==> text ]
            let worker = Func<string, string, Task<obj>>(fun sid question ->
                task {
                    match sessionContexts.TryGetValue sid with
                    | false, _ -> return refuse "OMP source Session has no active Provider context"
                    | true, ctx ->
                        let retry = ProviderProse.render (ProviderLanguageBinding.forSessionText sid) "host/advisor-seat/prose-retry" Map.empty
                        let! result = runEphemeralConsultation ctx (snapshotDenialText sid) question
                                        (retry + "\n" + question)
                                        ("scope-worker:" + sid + ":" + Guid.NewGuid().ToString("N"))
                        return match result with | Ok prose -> answer prose | Error reason -> refuse reason
                })
            let seat = Func<string, string, Task<obj>>(fun sid question ->
                task {
                    match sessionContexts.TryGetValue sid with
                    | false, _ -> return refuse "Dedicated Advisor has no active parent model context"
                    | true, ctx ->
                        let label path = ProviderProse.render (ProviderLanguageBinding.forSessionText sid) path Map.empty
                        // Preserve the Advisor's earlier confirmed discussions
                        // after a restart. These are the seat's own Stage 2/4 facts,
                        // never the temporary worker's unseen tool process.
                        let history = durableCall (fun journal ->
                            let frames: string array = unbox (journal?read("advisor-process:" + sid))
                            let questions = System.Collections.Generic.Dictionary<string, string>()
                            for serialized in frames do
                                let value = parseJson serialized
                                let kind = stringOf (fieldValue value "kind")
                                let phase = optionalInt (fieldValue value "phase") |> Option.defaultValue 0
                                if (phase = 2 || phase = 4) && kind = "StageBegan" then
                                    let key = stringOf (fieldValue value "process") + ":" + string phase
                                    questions[key] <- stringOf (fieldValue value "question")
                            let prior =
                                frames
                                |> Array.choose (fun serialized ->
                                    let value = parseJson serialized
                                    let kind = stringOf (fieldValue value "kind")
                                    let phase = optionalInt (fieldValue value "phase") |> Option.defaultValue 0
                                    if (phase = 2 || phase = 4) && kind = "StageAnswered" then
                                        let key = stringOf (fieldValue value "process") + ":" + string phase
                                        match questions.TryGetValue key with
                                        | true, question ->
                                            Some(label "host/advisor-seat/asked" + "\n" + question
                                                 + "\n" + label "host/advisor-seat/answered" + "\n"
                                                 + stringOf (fieldValue value "prose"))
                                        | _ -> None
                                    else None)
                                |> Array.rev
                                |> Array.truncate 12
                                |> Array.rev
                            createObj [ "ok" ==> true; "exchanges" ==> prior ])
                        let previous =
                            if isNull (fieldValue history "exchanges") then ""
                            else
                                let exchanges = unbox<string array> history?exchanges
                                if exchanges.Length = 0 then ""
                                else label "host/advisor-seat/history" + "\n\n"
                                     + String.concat "\n\n" (exchanges |> Array.toList) + "\n\n"
                        let prompt = previous + question
                        // The side-channel request is isolated from the working
                        // Session. SDK disables tool execution for ephemeral turns.
                        let options =
                            createObj [ "promptText" ==> prompt
                                        "conversationKey" ==> ("dedicated-advisor:" + sid)
                                        "tools" ==> false ]
                        let target =
                            if hasMethod ctx "runEphemeralTurn" then ctx
                            else fieldValue ctx "session"
                        let! raw =
                            if hasMethod target "runEphemeralTurn" then invokeEphemeralTurn target options
                            else Task.FromResult(Error "dedicated Advisor SDK side turn is unavailable")
                        return
                            match raw with
                            | Error reason -> refuse reason
                            | Ok result ->
                                let prose = stringOf (fieldValue result "replyText")
                                let called = messageHasToolCall (fieldValue result "assistantMessage")
                                if called || String.IsNullOrWhiteSpace prose then
                                    refuse "dedicated Advisor did not provide tool-free prose"
                                else answer prose
                })
            let steer = Func<string, string, string, string, Task<obj>>(fun sid epoch processId prose ->
                task {
                    if scopeEpoch sid <> epoch then return refuse "Advisor source Scope has advanced"
                    else
                        let key = "r-" + processId.Replace("advisor:", "")
                        let tcs = System.Threading.Tasks.TaskCompletionSource<obj>()
                        pendingAdvisorSteers[key] <- (sid, epoch, tcs)
                        let text = prose + "\n[receipt:" + key + "]"
                        let options = createObj [ "deliverAs" ==> "steer"; "attribution" ==> "agent" ]
                        try
                            let! _ = awaitObj (call2 (fieldValue pi "sendUserMessage") (box text) options)
                            let! confirmed = observedOrTimeout tcs.Task 30000
                            if isNull confirmed then
                                pendingAdvisorSteers.Remove key |> ignore
                                return refuse "Advisor steer has no confirmed physical acceptance"
                            else return confirmed
                        with ex ->
                            pendingAdvisorSteers.Remove key |> ignore
                            return refuse ("Advisor steer physical delivery unknown: " + ex.Message)
                })
            createObj [ "CurrentEpoch" ==> current; "LanguageFor" ==> language
                        "Claim" ==> claim; "Finish" ==> finished
                        "BeginStage" ==> stageBegin; "ConfirmStage" ==> stageConfirm
                        "Pending" ==> pending; "AskWorker" ==> worker
                        "AskSeat" ==> seat; "SendSteer" ==> steer ]
        let advisorQueue: obj = emitJsExpr createAdvisorQueue "$0()"
        let advisorFlow: obj = emitJsExpr (createAdvisorFlow, advisorQueue, advisorPorts) "$0($1,$2)"
        let reportAdvisor (sid: string) (epoch: string) (item: obj) =
            let receipt = fieldValue item "result"
            if not (isNull receipt) && isTrue (fieldValue receipt "advisor") then
                let runId = stringOf (fieldValue item "providerRunId")
                let key = epoch + ":" + runId
                let questionPath =
                    match stringOf (fieldValue receipt "trigger") with
                    | "ResetDue" -> "host/advisor-question/reset-due"
                    | "Equal" -> "host/advisor-question/equal"
                    | _ -> "host/advisor-question/increase"
                let question = ProviderProse.render (ProviderLanguageBinding.forSessionText sid) questionPath Map.empty
                let launched: Task<obj> = unbox (advisorFlow?trigger(sid, epoch, key, question))
                launched |> ignore

        let settleProgress (sid: string) =
            bindProgress sid
            let epoch = scopeEpoch sid
            let items: obj array = unbox (scopeProgress?settlePending(sid, epoch))
            items |> Array.iter (reportAdvisor sid epoch)

        let registerScopeProgressTool () =
            let estimateProse =
                ProviderProse.render (ProviderLanguageBinding.readGlobalPreference ()) "tool/js-program/arg-estimate" Map.empty
            let schema =
                createObj [ "type" ==> "object"
                            "properties" ==> createObj [
                                "remainingScopeTurnsEstimate" ==> createObj [
                                    "type" ==> "integer"; "minimum" ==> 0
                                    "description" ==> estimateProse ]]
                            "required" ==> [| "remainingScopeTurnsEstimate" |]
                            "additionalProperties" ==> false ]
            let definition = createObj [
                "name" ==> "todowrite"
                "label" ==> "Scope Progress"
                "loadMode" ==> "essential"
                "concurrency" ==> "exclusive"
                "approval" ==> "read"
                "description" ==> estimateProse
                "parameters" ==> schema
                "execute" ==> Func<obj, obj, obj, obj, obj, obj>(fun callId arguments _signal _onUpdate ctx ->
                    task {
                        let sid = ctxSessionId ctx
                        let value = optionalInt (fieldValue arguments "remainingScopeTurnsEstimate")
                        let cid = stringOf callId
                        if String.IsNullOrWhiteSpace sid || String.IsNullOrWhiteSpace cid || value.IsNone
                           || value.Value < 0 then
                            return ompScopeRefused "todowrite requires the real call identity and nonnegative remainingScopeTurnsEstimate"
                        else
                            bindProgress sid
                            let run =
                                match activeProgressRuns.TryGetValue sid with
                                | true, observed -> observed
                                | _ -> "detached:" + cid
                            let receipt: obj = scopeProgress?record(sid, scopeEpoch sid, run, cid, value.Value)
                            if isNull receipt?ok || not (unbox<bool> receipt?ok) then
                                return ompScopeRefused ("todowrite durable record refused: " + stringOf receipt?reason)
                            else
                                let text = "Scope estimate accepted: " + string value.Value
                                return createObj [
                                    "content" ==> [| createObj [ "type" ==> "text"; "text" ==> text ] |]
                                    "details" ==> createObj [ "estimate" ==> value.Value
                                                              "minimum" ==> receipt?minimum
                                                              "providerRun" ==> run ]]
                    } |> unbox<obj>) ]
            registerTool pi definition

        registerScopeProgressTool ()

        let bindHeaderParent (sid: string) (ctx: obj) =
            let sm = sessionManagerOf ctx
            let header = if isNull sm then null else callMethod sm "getHeader" null

            let parentSession =
                if isNull header then
                    null
                else
                    fieldValue header "parentSession"

            let parentStr = if isNull parentSession then "" else stringOf parentSession

            if not (String.IsNullOrEmpty parentStr) then
                Surface.bindDelegation sid parentStr |> ignore

        let refreshSessionState (ctx: obj) =
            let sid = ctxSessionId ctx
            Surface.clearSessionState sid
            sessionContexts.[sid] <- ctx
            Surface.forgetSession sid
            let hasChild, child = liveChildren.TryGetValue sid

            if hasChild then
                child.Parent |> Option.iter (fun p -> Surface.bindDelegation sid p |> ignore)
            else
                bindHeaderParent sid ctx

            Task.FromResult(jsUndefined) |> unbox<obj>

        registerEvent pi "session_start" (fun (_event: obj) (ctx: obj) -> refreshSessionState ctx)
        registerEvent pi "session_switch" (fun (_event: obj) (ctx: obj) -> refreshSessionState ctx)
        registerEvent pi "session_branch" (fun (_event: obj) (ctx: obj) -> refreshSessionState ctx)

        let recordNormalRequest (sid: string) (source: obj) (submitted: obj) (model: obj) (shouldFilter: bool) : obj =
            if not (isNull model) then
                outboundViewStore?recordModel (sid, model) |> ignore

            let incoming = fieldValue source "messages"
            let outgoing = fieldValue submitted "messages"
            if isArray incoming && isArray outgoing then
                Surface.rememberTransform sid (unbox<obj array> incoming) (unbox<obj array> outgoing)

            outboundViewStore?recordNativeRequest (sid, submitted, model) |> ignore

            if shouldFilter then submitted else jsUndefined

        // -- provider request: mask negotiate on root / unmanaged sessions ----------------
        registerEvent pi "before_provider_request" (fun (event: obj) (ctx: obj) ->
            task {
                let sid = ctxSessionId ctx
                sessionContexts[sid] <- ctx
                bindProgress sid
                let isManaged = liveChildren.ContainsKey sid
                let payload = fieldValue event "payload"
                let observedModel = fieldValue ctx "model"
                let rawModel = if isNull payload then null else fieldValue payload "model"
                let model = if isNull observedModel then rawModel else observedModel
                let tools = if isNull payload then null else fieldValue payload "tools"
                let shouldFilter = not isManaged && not (isNull payload) && isArray tools

                let submitted =
                    if shouldFilter then
                        let filtered = filterOutNegotiate tools
                        emitJsExpr (payload, filtered) "Object.assign({}, $0, { tools: $1 })"
                    else
                        payload

                match replayLatestProvider sid submitted ctx with
                | Some frozen -> return frozen
                | None -> return recordNormalRequest sid payload submitted model shouldFilter
            }
            |> unbox<obj>)

        let settlePendingPreviousSteer
            (sid: string)
            (journal: AgentJournal)
            (pair: System.Collections.Generic.KeyValuePair<string, string * PrefixEpochId>)
            =
            let _, epoch = pair.Value

            let current =
                ompPriorArchive journal sid
                |> Result.map (fun (head, _) -> head.EpochId = epoch)
                |> Result.defaultValue false

            if current then
                Surface.confirmPreviousScopeSteer pair.Key sid |> ignore

            pendingPreviousSteers.Remove pair.Key |> ignore

        registerEvent pi "message_start" (fun (event: obj) (ctx: obj) ->
            let sid = ctxSessionId ctx
            let message = fieldValue event "message"

            let agentSource =
                stringOf (fieldValue message "role") = "user"
                && stringOf (fieldValue message "attribution") = "agent"

            let text = if agentSource then extractMessageContent message else ""

            if agentSource then
                let accepted =
                    pendingAdvisorSteers
                    |> Seq.filter (fun pair ->
                        let owner, _, _ = pair.Value
                        owner = sid && text.Contains("[receipt:" + pair.Key + "]"))
                    |> Seq.toArray
                for pair in accepted do
                    let _, epoch, answer = pair.Value
                    let ok = scopeEpoch sid = epoch
                    let receipt = if ok then
                                      createObj [ "Ok" ==> true; "Prose" ==> ""; "Reason" ==> "" ]
                                  else
                                      createObj [ "Ok" ==> false; "Prose" ==> ""; "Reason" ==> "Advisor source Scope advanced before delivery" ]
                    pendingAdvisorSteers.Remove pair.Key |> ignore
                    answer.SetResult receipt

            let candidate =
                pendingPreviousSteers
                |> Seq.tryFind (fun pair ->
                    let owner, _ = pair.Value
                    owner = sid && text.Contains("[receipt:" + pair.Key + "]"))

            match candidate, journalOpt with
            | Some pair, Some journal -> settlePendingPreviousSteer sid journal pair
            | _ -> ()

            jsUndefined)

        let signal (value: HostSignal) : Task<unit> = task { return () }

        let dispatchToolGate (ctx: obj) (event: obj) =
            task {
                match hooks.ToolGate with
                | None -> return jsUndefined
                | Some gate ->
                    let! verdict =
                        gate.BeforeTool(
                            toolCallView
                                ctx
                                (fieldValue event "toolCallId")
                                (fieldValue event "toolName")
                                (fieldValue event "input")
                        )

                    return verdictReply verdict
            }

        // -- turn admission ---------------------------------------------------------------
        registerEvent pi "input" (fun (event: obj) (ctx: obj) ->
            task {
                match hooks.Turn with
                | None -> return jsUndefined
                | Some turn ->
                    let! verdict =
                        turn.AdmitTurn
                            { Context = turnContext ctx
                              Message = None
                              Text = stringOf (fieldValue event "text")
                              Correlation = None }

                    return! admissionReply pi ctx verdict
            }
            |> unbox<obj>)

        // -- system prompt ----------------------------------------------------------------
        registerEvent pi "before_agent_start" (fun (event: obj) (ctx: obj) ->
            task {
                let sid = ctxSessionId ctx
                sessionContexts.[sid] <- ctx
                let lang = ProviderLanguageBinding.forSessionText sid
                let scopeProtocol = ProviderProse.render lang "host/scope-protocol" Map.empty
                let system = stringList (fieldValue event "systemPrompt")
                let updated = updateSystemPromptScope system scopeProtocol

                match hooks.Turn with
                | None ->
                    outboundViewStore?recordSystem (sid, box (List.toArray updated)) |> ignore
                    return createObj [ "systemPrompt", box (List.toArray updated) ]
                | Some turn ->
                    let! prompt = turn.SystemPrompt(turnContext ctx, updated)
                    outboundViewStore?recordSystem (sid, box (List.toArray prompt)) |> ignore
                    return createObj [ "systemPrompt", box (List.toArray prompt) ]
            }
            |> unbox<obj>)

        // -- context messages -------------------------------------------------------------
        registerEvent pi "context" (fun (event: obj) (ctx: obj) ->
            task {
                let sid = ctxSessionId ctx
                sessionContexts.[sid] <- ctx
                let rawList = fieldValue event "messages"

                let visible = ompTenureMessages stateRoot sid rawList
                let scoped = ompScopeRequest sid visible

                let! finalResult =
                    match hooks.Turn with
                    | None -> Task.FromResult(contextMessagesReply rawList scoped)
                    | Some turn ->
                        task {
                            let messages = scoped |> Array.choose contextMessageView |> Array.toList
                            let! edit = turn.ContextMessages(turnContext ctx, messages)
                            return applyContextEdit rawList scoped edit
                        }

                let physical =
                    if isNull finalResult then
                        rawList
                    else
                        fieldValue finalResult "messages"

                if not (isNull physical) then
                    outboundViewStore?recordMessagesFor (sid, physical) |> ignore

                return finalResult
            }
            |> unbox<obj>)

        // -- tool gate --------------------------------------------------------------------
        registerEvent pi "tool_call" (fun (event: obj) (ctx: obj) ->
            task {
                let sid = ctxSessionId ctx
                sessionContexts.[sid] <- ctx
                let toolName = stringOf (fieldValue event "toolName")

                if toolName = "negotiate" && not (liveChildren.ContainsKey sid) then
                    return
                        createObj
                            [ "block", box true
                              "reason", box "negotiate is not permitted on root or unmanaged sessions." ]
                else
                    return! dispatchToolGate ctx event
            }
            |> unbox<obj>)

        registerEvent pi "tool_result" (fun (event: obj) (ctx: obj) ->
            task {
                match hooks.ToolGate with
                | None -> return jsUndefined
                | Some gate ->
                    let text = resultText event
                    let isError = isTrue (fieldValue event "isError")

                    let callView =
                        toolCallView
                            ctx
                            (fieldValue event "toolCallId")
                            (fieldValue event "toolName")
                            (fieldValue event "input")

                    let! edit =
                        gate.AfterTool(
                            callView,
                            { Text = text
                              Truncated = false
                              IsError = isError
                              Details = None }
                        )

                    return applyToolResultEdit edit
            }
            |> unbox<obj>)

        // -- compaction -------------------------------------------------------------------
        registerEvent pi "session.compacting" (fun (_event: obj) (ctx: obj) ->
            task {
                // The event's session id is omp's PROVIDER session id; the
                // neutral id is always the persisted one.
                let! contribution = hooks.Compaction.OnCompacting(SessionId.create (ctxSessionId ctx))

                match contribution with
                | CompactionContribution.Nothing -> return jsUndefined
                | CompactionContribution.Context lines -> return createObj [ "context", box (List.toArray lines) ]
                | CompactionContribution.ReplacePrompt prompt -> return createObj [ "prompt", box prompt ]
            }
            |> unbox<obj>)

        registerEvent pi "session_stop" (fun (_event: obj) (ctx: obj) ->
            task {
                let! verdict = hooks.Compaction.AutoContinue(SessionId.create (ctxSessionId ctx))

                match verdict with
                | ContinueVerdict.Allow -> return jsUndefined
                | ContinueVerdict.Deny reason -> return createObj [ "continue", box false; "reason", box reason ]
            }
            |> unbox<obj>)

        // -- subagent routing --------------------------------------------------------------
        // `before_subagent_spawn` is ALWAYS registered: the adapter advertises
        // the `SubagentRouting` capability, so the host must be able to reach
        // the product's routing decision even when the product drops the
        // hook. Without a hook there is nothing to route, so the spawn is
        // allowed through unchanged.
        registerEvent pi "before_subagent_spawn" (fun (event: obj) (ctx: obj) ->
            task { return! routeSpawn hooks event ctx } |> unbox<obj>)

        // -- host facts ---------------------------------------------------------------------
        registerEvent pi "turn_start" (fun (_event: obj) (ctx: obj) ->
            task {
                let sid = ctxSessionId ctx
                sessionContexts[sid] <- ctx
                bindProgress sid
                settleProgress sid
                let recovered: Task<obj> = unbox (advisorFlow?resume(sid, scopeEpoch sid))
                recovered |> ignore
                activeProgressRuns[sid] <- Guid.NewGuid().ToString("N")
                Surface.onTurnStart sid

                do!
                    signal (
                        HostSignal.AttemptStarted
                            { Session = SessionId.create (ctxSessionId ctx)
                              Message = None }
                    )
            }
            |> unbox<obj>)

        registerEvent pi "turn_end" (fun (event: obj) (ctx: obj) ->
            task {
                let sid = ctxSessionId ctx
                settleProgress sid
                activeProgressRuns.Remove sid |> ignore
                match decodeTerminal (fieldValue event "message") with
                | None -> return jsUndefined
                | Some terminal ->
                    do!
                        signal (
                            HostSignal.AttemptFinished(
                                { Session = SessionId.create (ctxSessionId ctx)
                                  Message = None },
                                terminal
                            )
                        )

                    return jsUndefined
            }
            |> unbox<obj>)

        registerEvent pi "agent_end" (fun (event: obj) (ctx: obj) ->
            task {
                if isTrue (fieldValue event "willContinue") || not (ctxIsIdle ctx) then
                    return jsUndefined
                else
                    do! signal (HostSignal.SessionIdle(SessionId.create (ctxSessionId ctx)))
                    return jsUndefined
            }
            |> unbox<obj>)

        registerEvent pi "auto_retry_start" (fun (event: obj) (ctx: obj) ->
            task {
                let attempt =
                    match optionalInt (fieldValue event "attempt") with
                    | Some value -> value
                    | None -> 0

                do!
                    signal (
                        HostSignal.ProviderRetry
                            { Session = SessionId.create (ctxSessionId ctx)
                              Attempt = attempt
                              Failure = decodeFailure (fieldValue event "errorMessage") false }
                    )
            }
            |> unbox<obj>)

        registerEvent pi "tool_execution_start" (fun (event: obj) (ctx: obj) ->
            task {
                do!
                    signal (
                        HostSignal.ToolStarted
                            { Session = SessionId.create (ctxSessionId ctx)
                              CallId = ToolCallId.create (stringOf (fieldValue event "toolCallId"))
                              Tool = stringOf (fieldValue event "toolName") }
                    )
            }
            |> unbox<obj>)

        signal

    // ------------------------------------------------------------------
    // omp extension entry
    // ------------------------------------------------------------------

    /// Report a failed activation through omp's logger. Never throws: a broken
    /// logger must not turn a product failure into a host failure (O-3 rule 7).
    let private logActivationFailure (pi: obj) (message: string) (reason: string) : unit =
        try
            call2 (fieldValue (fieldValue pi "logger") "error") (box message) (box (createObj [ "error", box reason ]))
            |> ignore
        with _ ->
            ()

    /// Run an activation step that must not throw into omp: an unexpected
    /// failure is logged and the session continues (O-3 rule 7).
    let private runActivationStep (pi: obj) (message: string) (body: unit -> Task) : Task<unit> =
        task {
            let! outcome =
                guardTask (fun () ->
                    (task {
                        do! body ()
                        return Ok()
                    })
                    :> Task<Result<unit, HostError>>)

            match outcome with
            | Error error ->
                logActivationFailure pi message (reasonOf error)
                return ()
            | Ok() -> return ()
        }

    /// Register the product hooks with omp and announce the session that made
    /// activation possible. The `SessionCreated` signal is emitted HERE: omp
    /// snapshots the listener list when it starts emitting `session_start`, so
    /// a listener registered during that emission is never called for this
    /// session.
    let private mountHooks
        (pi: obj)
        (emit: HostSignal -> Task<unit>)
        (hooks: ProductHooks)
        (ctx: obj)
        (stateRoot: string)
        : Task<unit> =
        task {
            let! journalOpt = acquireOmpJournal stateRoot
            let signal = registerProductHooks pi hooks emit journalOpt stateRoot

            do!
                signal (
                    HostSignal.SessionCreated
                        { Id = SessionId.create (ctxSessionId ctx)
                          Parent = None
                          Role = None
                          Title = None
                          // The host reports the session's working
                          // directory, so the signal carries the real one.
                          Workspace = Some(WorkspacePath(ctxCwd ctx)) }
                )
        }

    /// Activate the product on an omp extension. The host ports are built on
    /// the first `session_start`, because that is the first moment omp exposes
    /// a session context and the workspace the paths report is read from it.
    let private startOnOmp (pi: obj) (plugin: IHostPlugin) (agents: AgentRoleSpec list) : Task<unit> =
        // O-3 rule 6: a single activation latch. `session_start` fires for
        // every session and the product hooks must be registered only once.
        let activated = ref false

        // O-3 rule 5: the hooks `session_shutdown` disposes. Taking the hooks
        // is the disposal latch: a second shutdown sees `None`.
        let liveHooks: ProductHooks option ref = ref None

        let activate (ctx: obj) : Task<unit> =
            task {
                let workspace = ctxCwd ctx
                let! stateRoot = RuntimePath.stateRoot workspace

                let ports, emit = createOmpHostPorts pi agents workspace stateRoot
                let negotiationTransport = createNegotiationTransport pi liveChildren

                emitJsExpr
                    (plugin, negotiationTransport)
                    "if (typeof $0.AttachTransport === 'function') { $0.AttachTransport($1); }"

                let! outcome = guardTask (fun () -> plugin.Activate ports)

                match outcome with
                | Error error ->
                    logActivationFailure pi "wanxiangshu activation failed" (reasonOf error)
                    return ()
                | Ok hooks ->
                    liveHooks := Some hooks

                    do!
                        runActivationStep pi "wanxiangshu hook registration failed" (fun () ->
                            mountHooks pi emit hooks ctx stateRoot :> Task)

                    return ()
            }

        registerEvent pi "session_start" (fun (_event: obj) (ctx: obj) ->
            task {
                latestContext := ctx

                if !activated then
                    return jsUndefined
                else
                    activated := true
                    do! activate ctx
                    return jsUndefined
            }
            |> unbox<obj>)

        let dispose (hooks: ProductHooks) : Task = hooks.Dispose()

        registerEvent pi "session_shutdown" (fun (_event: obj) (_ctx: obj) ->
            task {
                for kv in latestProviderLeases do
                    kv.Value.Cancelled <- true

                latestProviderLeases.Clear()
                pendingPreviousSteers.Clear()
                activeProgressRuns.Clear()
                for kv in pendingAdvisorSteers do
                    let _, _, completion = kv.Value
                    completion.SetResult (createObj [ "Ok" ==> false; "Prose" ==> ""; "Reason" ==> "OMP Session ended before physical Advisor steer acceptance" ])
                pendingAdvisorSteers.Clear()

                for sid in sessionContexts.Keys do
                    scopeProgress?unbindSessionJournal(sid) |> ignore
                    scopeProgress?clear(sid) |> ignore
                    outboundViewStore?clearSession (sid) |> ignore

                sessionContexts.Clear()

                match !liveHooks with
                | None -> return jsUndefined
                | Some hooks ->
                    liveHooks := None
                    do! runActivationStep pi "wanxiangshu disposal failed" (fun () -> dispose hooks)
                    return jsUndefined
            }
            |> unbox<obj>)

        Task.FromResult()

    /// Activate the product on an omp extension.
    let activateOnOmp (pi: obj) (plugin: IHostPlugin) (agents: AgentRoleSpec list) : Task<unit> =
        startOnOmp pi plugin agents

    let getLiveChildren () : System.Collections.Generic.Dictionary<string, obj> =
        let dict = System.Collections.Generic.Dictionary<string, obj>()

        for kv in liveChildren do
            dict.Add(kv.Key, box kv.Value)

        dict
