namespace Wanxiangshu.OpenCode

open Wanxiangshu.Foundation.HostSession

open System
open System.Threading.Tasks
open Fable.Core
open Fable.Core.JsInterop
open FsToolkit.ErrorHandling
open Wanxiangshu.Foundation
open Wanxiangshu.Foundation.Identity
open Wanxiangshu.Host.Ports
open Wanxiangshu.Interaction.Dispatch

module SessionSnapshotPort =

    let private readString (value: obj) =
        if isNull value then
            None
        else
            let text = unbox<string> value
            if String.IsNullOrWhiteSpace text then None else Some text

    let private readFiniteNumber (value: obj) =
        if
            not (isNull value)
            && emitJsExpr value "typeof $0 === 'number' && Number.isFinite($0)"
        then
            Some(unbox<float> value)
        else
            None

    let private infoOf (raw: obj) =
        if isNull raw then null
        elif not (isNull raw?info) then raw?info
        else raw

    let private decodePartsOrEmpty (parts: obj array) : (MessagePart * HostMessagePartId option) array =
        try
            parts
            |> Array.choose (fun rawPart ->
                HostMessageCodec.decodePart rawPart
                |> Option.map (fun part ->
                    let partId = readString rawPart?id |> Option.map HostMessagePartId.create
                    part, partId))
        with _ ->
            [||]

    let private partsOf (raw: obj) : MessagePart array * HostMessagePartId option array =
        if isNull raw || isNull raw?parts then
            [||], [||]
        else
            let decoded = decodePartsOrEmpty (unbox<obj array> raw?parts)
            decoded |> Array.map fst, decoded |> Array.map snd

    let private canonicalValue (value: obj) =
        if isNull value then
            "null"
        else
            Wanxiangshu.Host.CanonicalJson.canonicalJson value

    let private isToolKind (kind: string) =
        kind = "tool"
        || kind = "tool-call"
        || kind = "tool_call"
        || kind.StartsWith "tool-"

    /// Evidence → Decision: tool-part input wire fields.
    let private toolInputOf (part: obj) (state: obj) =
        if not (isNull state) && not (isNull state?input) then
            state?input
        elif not (isNull part?input) then
            part?input
        elif not (isNull part?args) then
            part?args
        else
            part?arguments

    let private completedOutputOf (state: obj) =
        if isNull state then null
        elif not (isNull state?output) then state?output
        else state?result

    let private failedErrorOf (state: obj) =
        if isNull state then null
        elif not (isNull state?error) then state?error
        elif not (isNull state?errorText) then state?errorText
        else state?output

    /// Evidence → Decision: Host tool-part status string → SnapshotToolPartState.
    let private toolStateOf (state: obj) (stateValue: string) =
        match stateValue with
        | "completed" -> SnapshotToolPartState.Completed(canonicalValue (completedOutputOf state))
        | "error" -> SnapshotToolPartState.Failed(canonicalValue (failedErrorOf state))
        | _ -> SnapshotToolPartState.Pending

    let private sessionToolPartOf (partId: string) (call: string) (name: string) (part: obj) (state: obj) =
        let stateValue =
            readString (if isNull state then null else state?status)
            |> Option.map (fun value -> value.ToLowerInvariant())
            |> Option.defaultValue "pending"

        { HostToolPartId = HostToolPartId.create partId
          ToolCallId = ToolCallId.create call
          ToolName = name
          InputCanonical = canonicalValue (toolInputOf part state)
          State = toolStateOf state stateValue }

    /// Evidence → Decision: named tool identity present → SessionToolPart.
    let private chooseNamedToolPart (part: obj) : SessionToolPart option =
        let state = if isNull part?state then null else part?state

        let callId =
            readString part?toolCallId
            |> Option.orElse (readString part?callID)
            |> Option.orElse (readString part?callId)

        let toolName = readString part?tool |> Option.orElse (readString part?name)
        let hostToolPartId = readString part?id |> Option.orElse callId

        match hostToolPartId, callId, toolName with
        | Some partId, Some call, Some name -> Some(sessionToolPartOf partId call name part state)
        | _ -> None

    /// Evidence → Decision: part kind is a tool wire form.
    let private chooseToolKindPart (part: obj) : SessionToolPart option =
        let kind =
            readString part?``type``
            |> Option.defaultValue ""
            |> fun value -> value.ToLowerInvariant()

        if not (isToolKind kind) then
            None
        else
            chooseNamedToolPart part

    let private trySessionToolPart (part: obj) : SessionToolPart option =
        if isNull part then None else chooseToolKindPart part

    let private decodeToolPartsOrEmpty (parts: obj array) : SessionToolPart array =
        try
            parts |> Array.choose trySessionToolPart
        with _ ->
            [||]

    let private toolPartsOf (raw: obj) : SessionToolPart array =
        if isNull raw || isNull raw?parts then
            [||]
        else
            decodeToolPartsOrEmpty (unbox<obj array> raw?parts)

    let private modelOf (info: obj) : HostModel option =
        if isNull info then
            None
        elif not (isNull info?providerID) && not (isNull info?modelID) then
            Some
                { providerID = unbox<string> info?providerID
                  modelID = unbox<string> info?modelID
                  variant = None }
        elif not (isNull info?model) && not (isNull info?model?providerID) then
            Some
                { providerID = unbox<string> info?model?providerID
                  modelID = unbox<string> info?model?modelID
                  variant = None }
        else
            None

    let private errorNameOf (info: obj) (raw: obj) =
        // NOTE: this must stay plain sequential code. Fable miscompiles an
        // if-branch inside a list literal into a JS comma expression
        // `(value, [])` — the value is evaluated and discarded, so every
        // candidate reads as `None`. Measured on Host 1.18.9 error messages:
        // `info.error.name = "APIError"` decoded as `ErrorName = None`, which
        // turned a settled provider failure into `TurnUnknown`.
        let readError (error: obj) =
            readString (if isNull error then null else error?name)
            |> Option.orElseWith (fun () -> readString (if isNull error then null else error?``type``))

        readError (if isNull info then null else info?error)
        |> Option.orElseWith (fun () -> readError (if isNull raw then null else raw?error))
        |> Option.orElseWith (fun () -> readString (if isNull info then null else info?errorName))
        |> Option.orElseWith (fun () -> readString (if isNull raw then null else raw?errorName))

    /// `summary = true` in Host 1.18.9 is a boolean on assistant messages. User
    /// messages carry a summary OBJECT (`{ title?, body?, diffs }`), so a non-null
    /// test alone would treat every ordinary user message as a compaction
    /// pseudo-run — `judgeStartup` then refuses to start (HOST-006). Strict boolean
    /// equality is the only faithful reading of the SDK contract.
    let private isTrue (value: obj) =
        not (isNull value) && unbox<bool> value = true

    let private completedOf (info: obj) (raw: obj) =
        let timeOf (source: obj) =
            if isNull source || isNull source?time then
                null
            else
                source?time?completed

        not (isNull (timeOf info)) || not (isNull (timeOf raw))

    let private createdAtOf (info: obj) (raw: obj) =
        let createdOf (source: obj) =
            if isNull source || isNull source?time then
                None
            else
                readFiniteNumber source?time?created

        createdOf info |> Option.orElseWith (fun () -> createdOf raw)

    let private isCompactionOf (info: obj) (raw: obj) =
        // Same Fable constraint as `errorNameOf`: no if-branches inside list
        // literals (they compile to a discarded JS comma expression).
        let field name =
            match readString (if isNull info then null else info?(name)) with
            | Some value -> Some value
            | None -> readString (if isNull raw then null else raw?(name))

        field "agent" = Some "compaction"
        || field "mode" = Some "compaction"
        || isTrue (if isNull info then null else info?summary)
        || isTrue (if isNull raw then null else raw?summary)

    /// PROMPT-011 anchor, wherever the Host kept it.
    ///
    /// `OpenCodePort` writes it onto the text part because that survives the Host
    /// round-trip more reliably than `body.metadata`; both spellings are read here so
    /// the recovery does not depend on which one a given Host version preserved.
    let private promptKeyFromMetadata (source: obj) =
        if isNull source || isNull source?metadata then
            None
        else
            readString source?metadata?(PromptMetadataCodec.PromptKeyField)

    let private promptKeyFromPart (part: obj) =
        if isNull part then None else promptKeyFromMetadata part

    let private tryPickPromptKeyFromParts (parts: obj array) =
        try
            parts |> Array.tryPick promptKeyFromPart
        with _ ->
            None

    let private promptKeyFromParts (raw: obj) =
        if isNull raw || isNull raw?parts then
            None
        else
            tryPickPromptKeyFromParts (unbox<obj array> raw?parts)

    let private promptKeyOf (info: obj) (raw: obj) =
        promptKeyFromMetadata info
        |> Option.orElseWith (fun () -> promptKeyFromMetadata raw)
        |> Option.orElseWith (fun () -> promptKeyFromParts raw)

    let projectMessage (raw: obj) : SessionMessage option =
        if isNull raw then
            None
        else
            let info = infoOf raw

            match
                readString (if isNull info then null else info?id)
                |> Option.orElse (readString raw?id)
            with
            | None -> None
            | Some id ->
                let role =
                    readString (if isNull info then null else info?role)
                    |> Option.orElse (readString raw?role)
                    |> Option.defaultValue ""
                    |> fun value -> value.ToLowerInvariant()

                let agent =
                    readString (if isNull info then null else info?agent)
                    |> Option.orElse (readString raw?agent)

                let finish =
                    readString (if isNull info then null else info?finish)
                    |> Option.orElse (readString raw?finish)
                    |> Option.orElse (readString raw?finishReason)

                let parts, partIds = partsOf raw

                Some
                    { Id = id
                      Role = role
                      Agent = agent
                      Finish = finish
                      ErrorName = errorNameOf info raw
                      Model = modelOf info
                      ParentId =
                        readString (if isNull info then null else info?parentID)
                        |> Option.orElse (readString raw?parentID)
                      CreatedAt = createdAtOf info raw
                      Completed = completedOf info raw
                      IsCompaction = isCompactionOf info raw
                      PromptKey = promptKeyOf info raw
                      Parts = parts
                      PartIds = partIds
                      ToolParts = toolPartsOf raw }

    let projectMessages (rawMessages: obj array) =
        rawMessages |> Array.toList |> List.choose projectMessage

    [<Emit("Array.isArray($0)")>]
    let private isJsArray (value: obj) : bool = jsNative

    let private unwrapPayload (response: obj) : obj array =
        if isNull response then
            [||]
        elif isJsArray response then
            unbox<obj array> response
        elif not (isNull response?data) && isJsArray response?data then
            unbox<obj array> response?data
        elif
            not (isNull response?data)
            && not (isNull response?data?data)
            && isJsArray response?data?data
        then
            unbox<obj array> response?data?data
        else
            [||]

    /// HOST 1.18.9 `session.messages` defaults to `order = "desc"` unless pinned.
    /// Evidence: `packages/core/src/session.ts:308` (`requestedOrder = input.order ?? "desc"`).
    let private messagesQuery (workspaceDirectory: string option) =
        match workspaceDirectory with
        | Some dir -> createObj [ "directory", box dir; "order", box "asc" ]
        | None -> createObj [ "order", box "asc" ]

    let private messagesFromHttpText (text: string) =
        if String.IsNullOrWhiteSpace text then
            []
        else
            projectMessages (unwrapPayload (JS.JSON.parse text))


    type SdkSnapshotPort(client: obj, workspaceDirectory: string option) =
        let headersObj () =
            match workspaceDirectory with
            // OpenCode SDK encodes this header (see packages/sdk/js/src/client.ts).
            // Raw Unicode paths (e.g. 万象) are rejected by fetch Headers as invalid.
            | Some dir -> createObj [ "x-opencode-directory", box (emitJsExpr dir "encodeURIComponent($0)") ]
            | None -> createObj []

        interface ISessionSnapshotPort with
            member this.GetMessages(sessionId) =
                taskResult {
                    try
                        let sessObj = client?session
                        let messagesFn = sessObj?messages
                        do! Result.requireFalse "session.messages unavailable on SDK client" (isNull messagesFn)
                        let sid = SessionId.value sessionId

                        let payload =
                            createObj
                                [ "path", box (createObj [ "id", box sid ])
                                  "query", box (messagesQuery workspaceDirectory)
                                  "headers", box (headersObj ()) ]

                        let! response = unbox<Task<obj>> (messagesFn?call (sessObj, payload)) |> TaskResultCE.ofTask

                        return projectMessages (unwrapPayload response)
                    with ex ->
                        return! Error ex.Message
                }

    let private sessionIdOfRow (row: obj) : SessionId option =
        let value = if isNull row then null else row?id
        readString value |> Option.map SessionId.create

    let private exactRootTitle (title: string) (row: obj) =
        not (isNull row)
        && isNull row?parentID
        && not (isNull row?title)
        && unbox<string> row?title = title

    /// The manager session a candidate row carries, or the refusal it raises.
    let private managerSessionOfRow (row: obj) : Result<SessionId option, HostError> =
        if readString row?agent <> Some "manager" then
            Error(HostError.Refused "Delivery session does not identify the Manager agent")
        else
            sessionIdOfRow row
            |> Option.map (fun session -> Ok(Some session))
            |> Option.defaultWith (fun () -> Error(HostError.Refused "Delivery session has no valid identity"))

    /// One step of the row scan: the first refusal stops the scan.
    let private scanStep (acc: Result<SessionId list, HostError>) (row: obj) : Result<SessionId list, HostError> =
        acc
        |> Result.bind (fun sessions ->
            managerSessionOfRow row
            |> Result.map (fun found -> found |> Option.toList |> List.append sessions))

    /// The manager sessions among the candidate rows, or the first refusal a
    /// row carries.
    let private scanManagerSessions (rows: obj array) : Result<SessionId list, HostError> =
        rows |> Array.fold scanStep (Ok []) |> Result.map List.rev

    /// The manager sessions on a discovery page, or the ambiguity refusal.
    /// A full page cannot prove there is only one candidate, even when local
    /// filtering would leave one usable row.
    let private managerSessionsOnPage (title: string) (rows: obj array) : Result<SessionId list, HostError> =
        if rows.Length >= 2 then
            Error(HostError.Refused "Session discovery is ambiguous or its result page is full")
        else
            let matches = rows |> Array.filter (exactRootTitle title)

            scanManagerSessions matches

    let private discoveredSessions (title: string) (response: obj) : Result<SessionId list, HostError> =
        let rows =
            if not (isNull response) && not (isNull response?error) then
                Error(HostError.Refused "Session discovery returned an error response")
            elif isJsArray response then
                Ok(unbox<obj array> response)
            elif not (isNull response) && isJsArray response?data then
                Ok(unbox<obj array> response?data)
            else
                Error(HostError.Refused "Session discovery did not return a session list")

        match rows with
        | Error error -> Error error
        | Ok rows -> managerSessionsOnPage title rows

    [<Emit("encodeURIComponent($0)")>]
    let private encodeURIComponent (_value: string) : string = jsNative

    /// Host-neutral session verbs mapped onto the OpenCode host. The adapter
    /// owns the host payload shape; the product only ever sees port records.
    let private sessionInfoOfRow (row: obj) : SessionInfo option =
        sessionIdOfRow row
        |> Option.map (fun id ->
            { Id = id
              Parent = readString row?parentID |> Option.map SessionId.create
              Role = readString row?agent |> Option.map AgentRole
              Title = readString row?title
              Workspace = readString row?directory |> Option.map WorkspacePath })

    let private sessionInfosOfRows (rows: obj array) : SessionInfo list =
        rows |> Array.choose sessionInfoOfRow |> Array.toList

    /// A single host object, unwrapped from the SDK's `data` envelopes.
    let private objectPayload (response: obj) : obj =
        if isNull response then
            null
        elif not (isNull response?data) && not (isNull response?data?data) then
            response?data?data
        elif not (isNull response?data) then
            response?data
        else
            response

    let private parentOfPayload (payload: obj) : Result<SessionId option, HostError> =
        let session = objectPayload payload

        if isNull session then
            Error(HostError.Transport "Missing session response")
        else
            session?parentID |> readString |> Option.map SessionId.create |> Ok

    /// The HTTP status a transport response carries, when the transport
    /// exposes one. The SDK envelope keeps the raw response under `response`.
    let private responseStatus (response: obj) : int option =
        if isNull response then
            None
        elif not (isNull response?response) && not (isNull response?response?status) then
            Some(unbox<int> response?response?status)
        elif not (isNull response?status) then
            Some(unbox<int> response?status)
        elif not (isNull response?error) && not (isNull response?error?status) then
            Some(unbox<int> response?error?status)
        else
            None

    /// A transport status expressed in the port's error vocabulary.
    let private hostErrorOfStatus (status: int) (verb: string) : HostError =
        if status = 404 then
            HostError.NotFound(sprintf "%s: session not found (HTTP %d)" verb status)
        elif status >= 400 && status < 500 then
            HostError.Refused(sprintf "%s: host refused the request (HTTP %d)" verb status)
        else
            HostError.Transport(sprintf "%s: transport failure (HTTP %d)" verb status)

    /// The message a failed host response carries, when it carries one.
    let private failureMessage (response: obj) : string option =
        let error = if isNull response then null else response?error

        if isNull error then
            None
        elif not (isNull error?data) && not (isNull error?data?message) then
            readString error?data?message
        else
            readString error?message

    /// The failure a host response carries, expressed in the port's error
    /// vocabulary. The host payload itself never leaves the adapter.
    let private hostErrorOfResponse (response: obj) (verb: string) : HostError =
        let detail =
            match failureMessage response with
            | Some message -> message
            | None -> sprintf "%s failed" verb

        match responseStatus response with
        | Some status when status = 404 -> HostError.NotFound(sprintf "%s: %s" verb detail)
        | Some status when status >= 400 && status < 500 -> HostError.Refused(sprintf "%s: %s" verb detail)
        | Some status -> HostError.Transport(sprintf "%s: %s (HTTP %d)" verb detail status)
        | None -> HostError.Refused(sprintf "%s: %s" verb detail)

    /// The failure a transport status carries, when the status is a failure.
    let private statusFailure (response: obj) (verb: string) : HostError option =
        match responseStatus response with
        | Some status when status < 200 || status >= 300 -> Some(hostErrorOfStatus status verb)
        | _ -> None

    /// The failure an SDK response envelope carries, if it carries one. The
    /// SDK client reports a non-2xx answer as an error envelope rather than
    /// as a thrown exception.
    let private sdkFailure (response: obj) (verb: string) : HostError option =
        if isNull response then
            None
        elif not (isNull response?error) then
            Some(hostErrorOfResponse response verb)
        else
            statusFailure response verb

    /// The outcome of an SDK call: the host's failure, or the mapped answer.
    let private sdkOr (response: obj) (verb: string) (ok: unit -> Result<'T, HostError>) : Result<'T, HostError> =
        match sdkFailure response verb with
        | Some failure -> Error failure
        | None -> ok ()

    /// One SDK call, with any transport failure expressed in the port's
    /// error vocabulary.
    let private sdkInvoke (sessObj: obj) (fn: obj) (payload: obj) : Task<Result<obj, HostError>> =
        taskResult {
            try
                let! response = unbox<Task<obj>> (fn?call (sessObj, payload)) |> TaskResultCE.ofTask
                return! Ok response
            with ex ->
                return! Error(HostError.Transport ex.Message)
        }

    /// The SDK client's session-removal method: `session.delete` when the
    /// client exposes it, `session.close` otherwise.
    let private closeMethod (sessObj: obj) : obj =
        if isNull sessObj?delete then
            sessObj?close
        else
            sessObj?delete

    type SdkSessionPort(client: obj, workspaceDirectory: string option) =
        let headersObj (directory: string option) =
            match directory with
            | Some dir -> createObj [ "x-opencode-directory", box (emitJsExpr dir "encodeURIComponent($0)") ]
            | None -> createObj []

        interface ISessionPort with
            member _.FindByTitle(title) =
                taskResult {
                    let sessObj = client?session
                    let listFn = sessObj?list

                    do!
                        Result.requireFalse
                            (HostError.Transport "session.list unavailable on SDK client")
                            (isNull listFn)

                    let queryFields =
                        [ "roots", box true; "search", box title; "limit", box 2 ]
                        @ (workspaceDirectory
                           |> Option.map (fun dir -> [ "directory", box dir ])
                           |> Option.defaultValue [])

                    let payload =
                        createObj
                            [ "query", box (createObj queryFields)
                              "headers", box (headersObj workspaceDirectory) ]

                    let! response = sdkInvoke sessObj listFn payload

                    return! sdkOr response "session.list" (fun () -> discoveredSessions title response)
                }

            member _.Create(parent, spec) =
                taskResult {
                    let sessObj = client?session
                    let createFn = sessObj?create

                    do!
                        Result.requireFalse
                            (HostError.Transport "session.create unavailable on SDK client")
                            (isNull createFn)

                    let bodyFields =
                        (parent
                         |> Option.map (fun p -> [ "parentID", box (SessionId.value p) ])
                         |> Option.defaultValue [])
                        @ (spec.Title
                           |> Option.map (fun title -> [ "title", box title ])
                           |> Option.defaultValue [])
                        @ (spec.Role
                           |> Option.map (fun (AgentRole role) -> [ "agent", box role ])
                           |> Option.defaultValue [])

                    let directory =
                        spec.Workspace
                        |> Option.map (fun (WorkspacePath dir) -> dir)
                        |> Option.orElse workspaceDirectory

                    let payload =
                        createObj (
                            [ "body", box (createObj bodyFields); "headers", box (headersObj directory) ]
                            @ bodyFields
                        )

                    let! response = sdkInvoke sessObj createFn payload

                    return!
                        sdkOr response "session.create" (fun () ->
                            let created = objectPayload response

                            readString created?id
                            |> Option.map SessionId.create
                            |> Result.requireSome (HostError.Transport "Missing session id in response"))
                }

            member _.Parent(session) =
                taskResult {
                    let sessObj = client?session
                    let getFn = sessObj?get

                    do! Result.requireFalse (HostError.Transport "session.get unavailable on SDK client") (isNull getFn)

                    let sId = SessionId.value session

                    let payload =
                        createObj
                            [ "path", box (createObj [ "id", box sId ])
                              "sessionID", box sId
                              "headers", box (headersObj workspaceDirectory) ]

                    let! response = sdkInvoke sessObj getFn payload

                    return! sdkOr response "session.get" (fun () -> parentOfPayload response)
                }

            member _.Children(parent) =
                taskResult {
                    let sessObj = client?session
                    let childrenFn = sessObj?children

                    do!
                        Result.requireFalse
                            (HostError.Transport "session.children unavailable on SDK client")
                            (isNull childrenFn)

                    let pId = SessionId.value parent

                    let payload =
                        createObj
                            [ "path", box (createObj [ "id", box pId ])
                              "headers", box (headersObj workspaceDirectory) ]

                    let! response = sdkInvoke sessObj childrenFn payload

                    return!
                        sdkOr response "session.children" (fun () -> Ok(sessionInfosOfRows (unwrapPayload response)))
                }

            member _.Abort(session) =
                taskResult {
                    let sessObj = client?session
                    let abortFn = sessObj?abort

                    do!
                        Result.requireFalse
                            (HostError.Transport "session.abort unavailable on SDK client")
                            (isNull abortFn)

                    let sId = SessionId.value session

                    let payload =
                        createObj
                            [ "path", box (createObj [ "id", box sId ])
                              "headers", box (headersObj workspaceDirectory) ]

                    let! response = sdkInvoke sessObj abortFn payload

                    return! sdkOr response "session.abort" (fun () -> Ok())
                }

            // The OpenCode host (API v1) exposes exactly one stop action per
            // session: `session.abort` (POST /session/{id}/abort). It has no
            // attempt-scoped interrupt route — the only `session.interrupt`
            // in the host belongs to the v2 API generation, which this
            // adapter's client does not speak. The product's own
            // attempt-interrupt path already calls `session.abort` for the
            // session that owns the attempt (see OpenCodePort.fs), so the
            // attempt-scoped stop maps onto that same wire verb.
            member this.Interrupt(session) = (this :> ISessionPort).Abort session

            member _.Close(session) =
                taskResult {
                    let sessObj = client?session
                    let closeFn = closeMethod sessObj

                    do!
                        Result.requireFalse
                            (HostError.Transport "No close/delete session method on SDK client")
                            (isNull closeFn)

                    let sId = SessionId.value session

                    let payload =
                        createObj
                            [ "path", box (createObj [ "id", box sId ])
                              "sessionID", box sId
                              "headers", box (headersObj workspaceDirectory) ]

                    let! response = sdkInvoke sessObj closeFn payload

                    return! sdkOr response "session.delete" (fun () -> Ok())
                }

    type HttpSessionPort(baseUrl: string) =
        let cleanBase =
            if baseUrl.EndsWith("/") then
                baseUrl.Substring(0, baseUrl.Length - 1)
            else
                baseUrl

        [<Emit("fetch($0, $1)")>]
        let jsFetch (url: string) (init: obj) : Task<obj> = jsNative

        /// A response body, parsed when it can be parsed.
        let tryParse (text: string) : obj =
            try
                JS.JSON.parse text
            with _ ->
                null

        /// The message a parsed error body carries, when it carries one.
        let messageOf (parsed: obj) : string option =
            if not (isNull parsed?data) && not (isNull parsed?data?message) then
                readString parsed?data?message
            elif not (isNull parsed?message) then
                readString parsed?message
            else
                None

        /// The message a failed response body carries, when it carries one.
        let detailOfBody (text: string) : string option =
            let parsed =
                if String.IsNullOrWhiteSpace text then
                    null
                else
                    tryParse text

            if isNull parsed then None else messageOf parsed

        /// The detail a failed call reports, when the body carries none.
        let failureDetail (verb: string) (text: string) : string =
            match detailOfBody text with
            | Some message -> message
            | None -> sprintf "%s failed" verb

        /// A failed HTTP status expressed in the port's error vocabulary.
        let httpError (verb: string) (status: int) (detail: string) : HostError =
            if status = 404 then
                HostError.NotFound(sprintf "%s: %s" verb detail)
            elif status >= 400 && status < 500 then
                HostError.Refused(sprintf "%s: %s" verb detail)
            else
                HostError.Transport(sprintf "%s: %s (HTTP %d)" verb detail status)

        /// The body of a successful HTTP answer.
        let httpBody (text: string) : Result<obj, HostError> =
            let candidate =
                if String.IsNullOrWhiteSpace text then
                    createObj []
                else
                    tryParse text

            let usable = if isNull candidate then None else Some candidate

            match usable with
            | Some body -> Ok body
            | None -> Error(HostError.Transport "Host returned a body that is not valid JSON")

        /// One HTTP call, with its failure expressed in the port's error
        /// vocabulary. The raw host payload never leaves the adapter.
        let httpOutcome (verb: string) (status: int) (text: string) : Result<obj, HostError> =
            if status < 200 || status >= 300 then
                Error(httpError verb status (failureDetail verb text))
            else
                httpBody text

        /// The request payload of one HTTP call.
        let httpInit (method: string) (body: obj option) : obj =
            match body with
            | Some payload ->
                createObj
                    [ "method", box method
                      "headers", box (createObj [ "Content-Type", box "application/json" ])
                      "body", box (JS.JSON.stringify payload) ]
            | None -> createObj [ "method", box method ]

        /// The raw answer of one HTTP call: its status and its body text.
        let fetchRaw (url: string) (init: obj) : Task<Result<int * string, HostError>> =
            taskResult {
                try
                    let! response = jsFetch url init |> TaskResultCE.ofTask
                    let status = unbox<int> response?status
                    let! text = unbox<Task<string>> (response?text ()) |> TaskResultCE.ofTask
                    return! Ok((status, text))
                with ex ->
                    return! Error(HostError.Transport ex.Message)
            }

        /// One HTTP request, with its failure expressed in the port's error
        /// vocabulary. The raw host payload never leaves the adapter.
        let requestJson
            (verb: string)
            (method: string)
            (url: string)
            (body: obj option)
            : Task<Result<obj, HostError>> =
            let init = httpInit method body

            taskResult {
                let! raw = fetchRaw url init

                let (status, text) = raw

                return! httpOutcome verb status text
            }

        let readJson (verb: string) (url: string) : Task<Result<obj, HostError>> = requestJson verb "GET" url None

        let postJson (verb: string) (endpoint: string) (body: obj) : Task<Result<obj, HostError>> =
            requestJson verb "POST" (cleanBase + endpoint) (Some body)

        /// The host's session-removal verb: `DELETE /session/{id}`.
        let deleteSession (session: SessionId) : Task<Result<obj, HostError>> =
            requestJson "session.delete" "DELETE" (sprintf "%s/session/%s" cleanBase (SessionId.value session)) None

        interface ISessionPort with
            member _.FindByTitle(title) =
                taskResult {
                    let url =
                        sprintf "%s/session?roots=true&limit=2&search=%s" cleanBase (encodeURIComponent title)

                    let! data = requestJson "session.list" "GET" url None

                    return! discoveredSessions title data
                }

            member _.Create(parent, spec) =
                taskResult {
                    let bodyFields =
                        (parent
                         |> Option.map (fun p -> [ "parentID", box (SessionId.value p) ])
                         |> Option.defaultValue [])
                        @ (spec.Title
                           |> Option.map (fun title -> [ "title", box title ])
                           |> Option.defaultValue [])
                        @ (spec.Role
                           |> Option.map (fun (AgentRole role) -> [ "agent", box role ])
                           |> Option.defaultValue [])

                    let! data = postJson "session.create" "/session" (createObj bodyFields)

                    return!
                        readString (objectPayload data)?id
                        |> Option.map SessionId.create
                        |> Result.requireSome (HostError.Transport "Missing session id in response")
                }

            member _.Parent(session) =
                taskResult {
                    let! data = readJson "session.get" (sprintf "%s/session/%s" cleanBase (SessionId.value session))
                    return! parentOfPayload data
                }

            member _.Children(parent) =
                taskResult {
                    let! data =
                        readJson
                            "session.children"
                            (sprintf "%s/session/%s/children" cleanBase (SessionId.value parent))

                    return sessionInfosOfRows (unwrapPayload (objectPayload data))
                }

            member _.Abort(session) =
                taskResult {
                    let! _ =
                        postJson "session.abort" (sprintf "/session/%s/abort" (SessionId.value session)) (createObj [])

                    return ()
                }

            // The OpenCode host (API v1) exposes exactly one stop action per
            // session: `POST /session/{id}/abort`. It has no attempt-scoped
            // interrupt route — the only `session.interrupt` in the host
            // belongs to the v2 API generation, which this adapter does not
            // speak. The product's own attempt-interrupt path already calls
            // `session.abort` for the session that owns the attempt (see
            // OpenCodePort.fs), so the attempt-scoped stop maps onto that
            // same wire verb.
            member this.Interrupt(session) = (this :> ISessionPort).Abort session

            member _.Close(session) =
                taskResult {
                    let! _ = deleteSession session
                    return ()
                }

    type HttpSnapshotPort(baseUrl: string) =
        let cleanBase =
            if baseUrl.EndsWith("/") then
                baseUrl.Substring(0, baseUrl.Length - 1)
            else
                baseUrl

        [<Emit("fetch($0, $1)")>]
        let jsFetch (url: string) (init: obj) : Task<obj> = jsNative

        interface ISessionSnapshotPort with
            member _.GetMessages(sessionId) =
                taskResult {
                    try
                        let url =
                            sprintf "%s/session/%s/message?order=asc" cleanBase (SessionId.value sessionId)

                        let init = createObj [ "method", box "GET" ]
                        let! response = jsFetch url init |> TaskResultCE.ofTask
                        let status = unbox<int> response?status
                        do! Result.requireTrue (sprintf "HTTP %d" status) (status >= 200 && status < 300)
                        let! text = unbox<Task<string>> (response?text ()) |> TaskResultCE.ofTask
                        return messagesFromHttpText text
                    with ex ->
                        return! Error ex.Message
                }
