namespace Wanxiangshu.OpenCode

open System
open System.Threading.Tasks
open Fable.Core
open Fable.Core.JsInterop
open FsToolkit.ErrorHandling
open Wanxiangshu.Foundation
open Wanxiangshu.Foundation.Identity
open Wanxiangshu.Foundation
open Wanxiangshu.Foundation.Outcome

module OpenCodePortAdapter =

    let private wireModel (model: OpencodeModel) =
        createObj [ "providerID", box model.providerID; "modelID", box model.modelID ]

    let private modelRequestFields (model: OpencodeModel option) =
        match model with
        | None -> []
        | Some bound ->
            [ "model", box (wireModel bound) ]
            @ (bound.variant
               |> Option.map (fun variant -> [ "variant", box variant ])
               |> Option.defaultValue [])

    let private responseBody (res: obj) =
        if isNull res then
            null
        elif not (isNull res?data) && not (isNull res?data?data) then
            res?data?data
        elif not (isNull res?data) then
            res?data
        else
            res

    let private trySessionId (body: obj) =
        if not (isNull body) && not (isNull body?id) then
            Some(SessionId.create (unbox<string> body?id))
        else
            None

    let private optionalParentId (item: obj) =
        if isNull item?parentID then
            None
        else
            Some(SessionId.create (unbox<string> item?parentID))

    let private optionalAgent (item: obj) =
        if isNull item?agent then
            None
        else
            Some(unbox<string> item?agent)

    let private optionalTitle (item: obj) =
        if isNull item?title then
            None
        else
            Some(unbox<string> item?title)

    let private tryChildInfo (item: obj) =
        if isNull item || isNull item?id then
            None
        else
            Some
                { SessionId = SessionId.create (unbox<string> item?id)
                  ParentSessionId = optionalParentId item
                  Agent = optionalAgent item
                  Title = optionalTitle item }

    let private childrenFromResponse (data: obj) =
        let items: obj array = if isNull data then [||] else unbox data
        items |> Array.choose tryChildInfo |> Array.toList

    let private readParentId (body: obj) : Result<SessionId option, string> =
        if isNull body then
            Error "Missing session response"
        elif isNull body?parentID || String.IsNullOrWhiteSpace(string body?parentID) then
            Ok None
        else
            Ok(Some(SessionId.create (string body?parentID)))

    let private resolveCloseFn (sessObj: obj) =
        if not (isNull sessObj?delete) then Some sessObj?delete
        elif not (isNull sessObj?close) then Some sessObj?close
        else None

    let private parsePostSuccessBody (text: string) =
        if String.IsNullOrWhiteSpace text then
            createObj []
        else
            Fable.Core.JS.JSON.parse text

    let private parseGetBody (body: string) =
        if String.IsNullOrWhiteSpace body then
            box [||]
        else
            Fable.Core.JS.JSON.parse body

    let private tryNonBlankDirectory (input: obj) =
        let directory = unbox<string> input?directory

        if String.IsNullOrWhiteSpace directory then
            None
        else
            Some directory

    let private tryWorkDirectory (input: obj) =
        if isNull input || isNull input?directory then
            None
        else
            tryNonBlankDirectory input

    let private readResponseTextSafe (response: obj) : Task<string> =
        task {
            try
                return! unbox<Task<string>> (response?text ())
            with _ ->
                return ""
        }

    [<Emit("fetch($0, $1)")>]
    let private jsFetch (url: string) (init: obj) : Task<obj> = jsNative

    /// Deliver the eventual detached enqueue verdict to the owning dispatch
    /// layer — never adjudicated here. OwnedSettled is a no-op refusal to
    /// re-settle; Refused and OutcomeUnknown re-enter the session's own
    /// PromptKey handler. A caller that carried no listener has no owner for
    /// a late verdict, so the port records honest diagnostic evidence rather
    /// than claiming a fused invariant.
    type SdkClientPort(client: obj, workspaceDirectory: string option) =
        let headersObj (directory: string option) =
            match directory |> Option.orElse workspaceDirectory with
            // OpenCode SDK encodes this header (see packages/sdk/js/src/client.ts).
            // Raw Unicode paths (e.g. 万象) are rejected by fetch Headers as invalid.
            | Some dir -> createObj [ "x-opencode-directory", box (emitJsExpr dir "encodeURIComponent($0)") ]
            | None -> createObj []

        interface IOpenCodePort with
            member this.SendPrompt (sessionId: SessionId) text opts =
                task {
                    let sId = SessionId.value sessionId
                    // Host PromptInput has no top-level correlation field. Put
                    // wanxiangshu keys on TextPart.metadata so chat.message
                    // can recover them from output.parts even when body.metadata
                    // is stripped.
                    let parts =
                        match opts.Metadata with
                        | Some metadata ->
                            [| createObj [ "type", box "text"; "text", box text; "metadata", metadata ] |]
                        | None -> [| createObj [ "type", box "text"; "text", box text ] |]

                    let model = opts.Model

                    let bodyFields =
                        [ "parts", box parts ]
                        @ modelRequestFields model
                        @ (opts.Agent
                           |> Option.map (fun agent -> [ "agent", box agent ])
                           |> Option.defaultValue [])
                        @ (opts.Metadata
                           |> Option.map (fun metadata -> [ "metadata", metadata ])
                           |> Option.defaultValue [])
                        @ (opts.Tools
                           |> Option.map (fun tools ->
                               [ "tools",
                                 box (
                                     tools
                                     |> Map.toList
                                     |> List.map (fun (name, enabled) -> name, box enabled)
                                     |> createObj
                                 ) ])
                           |> Option.defaultValue [])

                    // v1 SDK: { path.id, body.agent }. v2 SDK: top-level sessionID/agent/model/parts.
                    // A nested-only payload drops agent on v2, and OpenCode then
                    // defaultInfo()s a Deep child onto Fast.
                    let payload =
                        createObj (
                            [ "path", box (createObj [ "id", box sId ])
                              "body", box (createObj bodyFields)
                              "headers", box (headersObj opts.Directory)
                              "sessionID", box sId
                              "parts", box parts ]
                            @ modelRequestFields model
                            @ (opts.Agent
                               |> Option.map (fun agent -> [ "agent", box agent ])
                               |> Option.defaultValue [])
                        )

                    try
                        let sessObj = client?session
                        let promptFn = sessObj?promptAsync
                        let pending = unbox<Task<obj>> (promptFn?call (sessObj, payload))
                        let! _ = pending
                        return AdmittedWithReceipt(TransportReceipt.create (sprintf "accepted-%s" sId))
                    with ex ->
                        return Fatal ex.Message
                }

            member this.AbortSession(sessionId: SessionId) =
                taskResult {
                    try
                        let sId = SessionId.value sessionId
                        let sessObj = client?session
                        let abortFn = sessObj?abort

                        let payload =
                            createObj [ "path", box (createObj [ "id", box sId ]); "headers", box (headersObj None) ]

                        let! _ = unbox<Task<obj>> (abortFn?call (sessObj, payload)) |> TaskResultCE.ofTask
                        return ()
                    with ex ->
                        return! Error ex.Message
                }

            member this.CreateSession (parentId: SessionId option) opts =
                taskResult {
                    let parentFields =
                        parentId
                        |> Option.map (fun parent -> [ "parentID", box (SessionId.value parent) ])
                        |> Option.defaultValue []

                    let bodyFields =
                        parentFields
                        @ (opts.Title
                           |> Option.map (fun title -> [ "title", box title ])
                           |> Option.defaultValue [])
                        @ (opts.Agent
                           |> Option.map (fun agent -> [ "agent", box agent ])
                           |> Option.defaultValue [])

                    let payload =
                        createObj (
                            [ "body", box (createObj bodyFields)
                              "headers", box (headersObj opts.Directory) ]
                            @ bodyFields
                        )

                    try
                        let sessObj = client?session
                        let createFn = sessObj?create
                        let! res = unbox<Task<obj>> (createFn?call (sessObj, payload)) |> TaskResultCE.ofTask

                        return!
                            responseBody res
                            |> trySessionId
                            |> Result.requireSome "Missing session id in response"
                    with ex ->
                        return! Error ex.Message
                }

            member this.GetSessionParent(sessionId: SessionId) =
                taskResult {
                    try
                        let sId = SessionId.value sessionId
                        let sessObj = client?session
                        let getFn = sessObj?get

                        let payload =
                            createObj
                                [ "path", box (createObj [ "id", box sId ])
                                  "sessionID", box sId
                                  "headers", box (headersObj None) ]

                        let! res = unbox<Task<obj>> (getFn?call (sessObj, payload)) |> TaskResultCE.ofTask
                        return! responseBody res |> readParentId
                    with ex ->
                        return! Error ex.Message
                }

            member this.CreateChildSession (parentId: SessionId) opts =
                taskResult {
                    let pId = SessionId.value parentId

                    let payload =
                        createObj
                            [ "body",
                              box
                                  {| parentID = pId
                                     title = opts.Title
                                     agent = opts.Agent |}
                              "headers", box (headersObj opts.Directory)
                              "parentID", box pId
                              "title", box opts.Title
                              "agent", box opts.Agent ]

                    try
                        let sessObj = client?session
                        let createFn = sessObj?create
                        let! res = unbox<Task<obj>> (createFn?call (sessObj, payload)) |> TaskResultCE.ofTask

                        return!
                            responseBody res
                            |> trySessionId
                            |> Result.requireSome "Missing session id in response"
                    with ex ->
                        return! Error ex.Message
                }

            member this.ListChildren(parentId: SessionId) =
                taskResult {
                    try
                        let pId = SessionId.value parentId
                        let sessObj = client?session
                        let childrenFn = sessObj?children

                        let payload =
                            createObj [ "path", box (createObj [ "id", box pId ]); "headers", box (headersObj None) ]

                        let! res = unbox<Task<obj>> (childrenFn?call (sessObj, payload)) |> TaskResultCE.ofTask
                        return responseBody res |> childrenFromResponse
                    with ex ->
                        return! Error ex.Message
                }

            member this.CloseChildSession(childId: SessionId) =
                taskResult {
                    try
                        let cId = SessionId.value childId
                        let sessObj = client?session

                        let! closeFn =
                            resolveCloseFn sessObj
                            |> Result.requireSome "No close/delete session method on SDK client"

                        let! _ =
                            unbox<Task<obj>> (closeFn?call (sessObj, {| sessionID = cId |}))
                            |> TaskResultCE.ofTask

                        return ()
                    with ex ->
                        return! Error ex.Message
                }

    type HttpPort(baseUrl: string) =
        let cleanBaseUrl =
            if baseUrl.EndsWith("/") then
                baseUrl.Substring(0, baseUrl.Length - 1)
            else
                baseUrl

        let postJson (endpoint: string) (body: obj) : Task<Result<obj, string>> =
            taskResult {
                try
                    let init =
                        {| method = "POST"
                           headers = {| ``Content-Type`` = "application/json" |}
                           body = Fable.Core.JS.JSON.stringify body |}

                    let! response = jsFetch (cleanBaseUrl + endpoint) init |> TaskResultCE.ofTask
                    let status = unbox<int> response?status
                    do! Result.requireTrue $"HTTP {status}" (status >= 200 && status < 300)
                    // Some Host endpoints answer 2xx with an empty body. Never
                    // treat a successful empty response as an error.
                    let! text = readResponseTextSafe response |> TaskResultCE.ofTask
                    return parsePostSuccessBody text
                with ex ->
                    return! Error ex.Message
            }

        let getJson (endpoint: string) : Task<Result<obj, string>> =
            taskResult {
                try
                    let! response = jsFetch (cleanBaseUrl + endpoint) {| method = "GET" |} |> TaskResultCE.ofTask
                    let status = unbox<int> response?status
                    do! Result.requireTrue $"HTTP {status}" (status >= 200 && status < 300)
                    let! body = unbox<Task<string>> (response?text ()) |> TaskResultCE.ofTask
                    return parseGetBody body
                with ex ->
                    return! Error ex.Message
            }

        interface IOpenCodePort with
            member _.SendPrompt (sessionId: SessionId) text opts =
                task {
                    let sId = SessionId.value sessionId
                    // Omit optional fields when absent: host rejects model:null.
                    // Correlation metadata lives on the text part (host-stable).
                    let parts =
                        match opts.Metadata with
                        | Some metadata ->
                            [| createObj [ "type", box "text"; "text", box text; "metadata", metadata ] |]
                        | None -> [| createObj [ "type", box "text"; "text", box text ] |]

                    let model = opts.Model

                    let bodyFields =
                        [ "parts", box parts ]
                        @ modelRequestFields model
                        @ (opts.Agent
                           |> Option.map (fun agent -> [ "agent", box agent ])
                           |> Option.defaultValue [])
                        @ (opts.Metadata
                           |> Option.map (fun metadata -> [ "metadata", metadata ])
                           |> Option.defaultValue [])
                        @ (opts.Tools
                           |> Option.map (fun tools ->
                               [ "tools",
                                 box (
                                     tools
                                     |> Map.toList
                                     |> List.map (fun (name, enabled) -> name, box enabled)
                                     |> createObj
                                 ) ])
                           |> Option.defaultValue [])

                    let! response = postJson $"/session/{sId}/prompt_async" (createObj bodyFields)

                    let classifyPostError (error: string) =
                        if error.StartsWith("HTTP ", System.StringComparison.Ordinal) then
                            Fatal error
                        else
                            AcceptanceUnknown error

                    match response with
                    | Ok _ -> return AdmittedWithReceipt(TransportReceipt.create (sprintf "accepted-%s" sId))
                    | Error error -> return classifyPostError error
                }

            member _.AbortSession(sessionId: SessionId) =
                taskResult {
                    let sId = SessionId.value sessionId
                    let! _ = postJson $"/session/{sId}/abort" {| |}
                    return ()
                }

            member _.CreateSession (parentId: SessionId option) opts =
                taskResult {
                    let bodyFields =
                        (parentId
                         |> Option.map (fun parent -> [ "parentID", box (SessionId.value parent) ])
                         |> Option.defaultValue [])
                        @ (opts.Title
                           |> Option.map (fun title -> [ "title", box title ])
                           |> Option.defaultValue [])
                        @ (opts.Agent
                           |> Option.map (fun agent -> [ "agent", box agent ])
                           |> Option.defaultValue [])

                    let! data = postJson "/session" (createObj bodyFields)
                    return! trySessionId data |> Result.requireSome "Missing session id in response"
                }

            member _.GetSessionParent(sessionId: SessionId) =
                taskResult {
                    let! data = getJson $"/session/{SessionId.value sessionId}"
                    return! readParentId data
                }

            member _.CreateChildSession (parentId: SessionId) opts =
                taskResult {
                    let pId = SessionId.value parentId

                    let bodyFields =
                        [ "parentID", box pId ]
                        @ (opts.Title
                           |> Option.map (fun title -> [ "title", box title ])
                           |> Option.defaultValue [])
                        @ (opts.Agent
                           |> Option.map (fun agent -> [ "agent", box agent ])
                           |> Option.defaultValue [])

                    let! data = postJson "/session" (createObj bodyFields)
                    return! trySessionId data |> Result.requireSome "Missing session id in response"
                }

            member _.ListChildren(parentId: SessionId) =
                taskResult {
                    let! data = getJson $"/session/{SessionId.value parentId}/children"

                    try
                        return childrenFromResponse data
                    with ex ->
                        return! Error ex.Message
                }

            member _.CloseChildSession(childId: SessionId) =
                taskResult {
                    let cId = SessionId.value childId
                    let! _ = postJson $"/session/{cId}/abort" {| |}
                    return ()
                }

    let create (input: obj) : IOpenCodePort option =
        let workDir = tryWorkDirectory input

        if isNull input then
            None
        elif not (isNull input?client) && not (isNull input?client?session) then
            Some(SdkClientPort(input?client, workDir) :> IOpenCodePort)
        elif not (isNull input?serverUrl) then
            Some(HttpPort(unbox<string> input?serverUrl) :> IOpenCodePort)
        elif not (isNull input?baseUrl) then
            Some(HttpPort(unbox<string> input?baseUrl) :> IOpenCodePort)
        elif not (isNull input?port) then
            let portNum = unbox<int> input?port
            Some(HttpPort($"http://127.0.0.1:{portNum}") :> IOpenCodePort)
        else
            None
