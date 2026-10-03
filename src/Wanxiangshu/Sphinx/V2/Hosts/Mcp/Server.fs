namespace Wanxiangshu.Sphinx.V2.Hosts

open System
open System.Threading.Tasks
open Fable.Core
open Fable.Core.JsInterop
open Wanxiangshu.Foundation
open Wanxiangshu.Foundation.Identity
open Wanxiangshu.Host
open Wanxiangshu.Persistence.EventStore
open Wanxiangshu.Sphinx.V2.Core
open Wanxiangshu.Sphinx.V2.Composition
open Wanxiangshu.Sphinx.V2.Persistence
open Wanxiangshu.Sphinx.V2.Runtime
open Wanxiangshu.Sphinx.V2.Wire

/// The MCP server adapter for Sphinx v2.
///
/// WHAT[sphinx-v2-009]: the SDK lives only here. Every tool call decodes into its own
/// domain input and then goes to the one Runtime; a handler never judges an epistemic
/// stage and never assembles a state of its own.
///
/// WHAT[sphinx-v2-035]: Sphinx apiVersion is a business contract, separate from the
/// MCP protocol revision this adapter is built against. awaiting_results is the
/// content of a completed tool result, not a protocol-level request for user input.
///
/// WHAT[sphinx-v2-034]: an unconfirmed abort stays cancelling. The fold records the
/// request; only the Host can report a physical terminal.
///
/// WHAT[sphinx-v2-036]: the seven public tools. A read-only tool creates no lease,
/// calls no model and changes no business state.
module Mcp =

    [<Import("McpServer", "@modelcontextprotocol/sdk/server/mcp.js")>]
    let private mcpServerConstructor: obj = jsNative

    [<Import("StdioServerTransport", "@modelcontextprotocol/sdk/server/stdio.js")>]
    let private stdioTransportConstructor: obj = jsNative

    [<Import("z", "zod")>]
    let private zod: obj = jsNative

    [<Emit("new $0($1, $2)")>]
    let private construct (constructor: obj) (info: obj) (options: obj) : obj = jsNative

    [<Emit("new $0()")>]
    let private constructEmpty (constructor: obj) : obj = jsNative

    [<Emit("$0.string().describe($1)")>]
    let private zString (z: obj) (description: string) : obj = jsNative

    // MCP's SDK validates tool arguments against the input schema before the
    // handler runs, and zod objects strip unknown keys by default. The decode
    // layer must see forbidden fields (work_submit's certificatePatches,
    // status's smuggled commands) to refuse them by name, so every input
    // schema becomes a passthrough zod object instead of silently dropping
    // unknown keys.
    [<Emit("$0.object($1).passthrough()")>]
    let private zObjectPassthrough (z: obj) (shape: obj) : obj = jsNative

    [<Emit("$0.string().optional().describe($1)")>]
    let private zOptionalString (z: obj) (description: string) : obj = jsNative

    [<Emit("$0.number().int().min(1).describe($1)")>]
    let private zCount (z: obj) (description: string) : obj = jsNative

    [<Emit("$0.array($1).describe($2)")>]
    let private zList (z: obj) (item: obj) (description: string) : obj = jsNative

    [<Emit("$0.object({ id: $1, hash: $2 })")>]
    let private zSchemaRef (z: obj) (id: obj) (hash: obj) : obj = jsNative

    [<Emit("$0.registerTool($1, $2, $3)")>]
    let private registerTool (server: obj) (name: string) (config: obj) (handler: obj) : obj = jsNative

    // Wrap every tool result as a real MCP result. Thoth's Encode.record emits
    // FSharpMap values, which the MCP SDK's zod validation rejects ("expected
    // record, received FSharpMap"), so the wrapper first deep-converts Fable
    // maps into plain JSON objects. content carries the readable JSON,
    // structuredContent carries the business payload, and a refusal is an
    // explicit isError=true.
    [<Emit("(args) => $0(args).then((raw) => { const toPlain = (v) => { if (v === null || typeof v !== 'object') { return v } if (Array.isArray(v)) { return v.map(toPlain) } if (typeof v.entries === 'function' && typeof v.get === 'function') { const o = {}; for (const entry of v.entries()) { o[entry[0]] = toPlain(entry[1]) } return o } return v }; const payload = toPlain(raw); return { content: [{ type: 'text', text: JSON.stringify(payload) }], isError: payload.outcome === 'refused', structuredContent: payload } })")>]
    let private unaryHandler (handler: obj -> Task<obj>) : obj = jsNative

    [<Emit("$0.connect($1)")>]
    let private connect (server: obj) (transport: obj) : JS.Promise<unit> = jsNative

    /// A settled promise carrying unit, used on the failure path where there is
    /// nothing left to await.
    [<Emit("Promise.resolve()")>]
    let private resolved: JS.Promise<unit> = jsNative

    [<Emit("console.error($0)")>]
    let private consoleError (line: string) : unit = jsNative

    let private classify = Wanxiangshu.Sphinx.V2.Runtime.Surface.classifyOutcome

    /// The published inquiry state, read through the one canonical key.
    let private currentState (store: IEventStore) (inquiryId: InquiryId) : Result<InquiryState option, CurrentError> =
        Bind.tryInquiry store inquiryId

    let private currentRefusal (fault: CurrentError) : ToolRefusal =
        match fault with
        | CurrentError.DomainConflict _ ->
            { Code = "DOMAIN_CONFLICT"
              Path = "inquiryId"
              Message = "inquiry has multiple legitimate durable heads; no resolution command is available" }
        | CurrentError.SemanticRejected reason ->
            { Code = "PERSISTENCE_SEMANTIC_CUT"
              Path = "inquiryId"
              Message = reason }

    /// A refusal result: the call changed nothing, and it carries no business fact.
    let private refused (refusal: ToolRefusal) : Task<obj> =
        task {
            return
                Encode.record
                    [ ("apiVersion", box Contract.apiVersion)
                      ("outcome", box "refused")
                      ("refusal",
                       Encode.record
                           [ ("code", box refusal.Code)
                             ("path", box refusal.Path)
                             ("message", box refusal.Message) ]) ]
        }

    /// None means the inquiry is not in the durable record. It never means an empty
    /// inquiry, so a read says so instead of inventing one.
    let private unknownInquiry (inquiryId: string) : ToolRefusal =
        { Code = "UNKNOWN_INQUIRY"
          Path = "inquiryId"
          Message = sprintf "inquiry %s is not in the durable record" inquiryId }

    let private commandRefusal (fault: CommandError) : ToolRefusal =
        { Code = fault.Code
          Path = "commandId"
          Message = fault.Message }

    let private appendRefusal (fault: AppendError) : ToolRefusal =
        let message =
            match fault with
            | AppendError.StorageInvalid invalid -> sprintf "the store refused the write as invalid: %A" invalid
            | AppendError.SemanticCut cut -> sprintf "semantic cut: %s" cut.Reason
            | AppendError.AppendFailed reason -> reason

        { Code = "PERSISTENCE_REJECTED"
          Path = "inquiryId"
          Message = message }

    let private cutRefusal (receipt: Wanxiangshu.Persistence.EventStore.AppendReceipt) : ToolRefusal =
        { Code = "PERSISTENCE_SEMANTIC_CUT"
          Path = "inquiryId"
          Message =
            receipt.Cuts
            |> List.map (fun cut -> sprintf "%s: %s" cut.Rule cut.Reason)
            |> String.concat "; " }

    let private previousHead (state: InquiryState) : Wanxiangshu.Sphinx.V2.Core.EventId option =
        state.EventHead |> Option.map (fun head -> head)

    /// A read-only status query. It creates no lease, never calls a model and never
    /// changes business state; the next step is the Runtime's own classification.
    let private statusResult (store: IEventStore) (args: StatusArgs) : Task<obj> =
        task {
            match currentState store (InquiryId.create args.InquiryId) with
            | Error fault -> return! refused (currentRefusal fault)
            | Ok None -> return! refused (unknownInquiry args.InquiryId)
            | Ok(Some found) ->
                return
                    Encode.record
                        [ ("apiVersion", box Contract.apiVersion)
                          ("outcome", box "read")
                          ("inquiryId", box args.InquiryId)
                          ("status", box (Encode.statusOf found))
                          ("revision", Encode.revision found.Revision)
                          ("advance", classify found)
                          ("inquiry", Encode.semanticView found) ]
        }

    /// A durable receipt and the readable Current are different facts. A fork formed
    /// during append must not be reported as one chosen branch, or as a failed write.
    let private cancellationReceipt (store: IEventStore) (args: CancelArgs) (revision: Revision) : obj =
        let receipt =
            [ ("apiVersion", box Contract.apiVersion)
              ("outcome", box "applied")
              ("inquiryId", box args.InquiryId)
              ("revision", Encode.revision revision) ]

        let refusedCurrent (refusal: ToolRefusal) =
            Encode.record (
                receipt
                @ [ ("currentRefusal",
                     Encode.record
                         [ ("code", box refusal.Code)
                           ("path", box refusal.Path)
                           ("message", box refusal.Message) ]) ]
            )

        match currentState store (InquiryId.create args.InquiryId) with
        | Error fault -> refusedCurrent (currentRefusal fault)
        | Ok None -> refusedCurrent (unknownInquiry args.InquiryId)
        | Ok(Some current) -> Encode.record (receipt @ [ ("status", box (Encode.statusOf current)) ])

    let private appendSealedCancellation
        (store: IEventStore)
        (encoded: EventEnvelope)
        (args: CancelArgs)
        (nextRevision: Revision)
        : Task<obj> =
        task {
            let! appended = store.Append [ encoded ]

            match appended with
            | Error fault -> return! refused (appendRefusal fault)
            | Ok receipt when not (List.isEmpty receipt.Cuts) -> return! refused (cutRefusal receipt)
            | Ok _ -> return cancellationReceipt store args nextRevision
        }

    let private sealAndAppendCancellation
        (store: IEventStore)
        (state: InquiryState)
        (args: CancelArgs)
        (nextRevision: Revision)
        (batch: TransitionBatch)
        : Task<obj> =
        match Codec.seal HostDigest.sha256Hex (Some state) batch with
        | Error fault ->
            refused
                { Code = fault.Code
                  Path = "commandId"
                  Message = fault.Message }
        | Ok encoded -> appendSealedCancellation store encoded args nextRevision

    let private cancelFreshAdmission
        (store: IEventStore)
        (state: InquiryState)
        (args: CancelArgs)
        (fingerprint: string)
        : Task<obj> =
        let head = previousHead state
        let nextRevision = Revision.next state.Revision

        let batch =
            { SchemaVersion = "2"
              InquiryId = state.Id
              PreviousRevision = state.Revision
              PreviousHead = head
              Revision = nextRevision
              CommandId = args.CommandId
              CommandFingerprint = fingerprint
              PostStateFingerprint = None
              Events = [ InquiryEventBody.CancelRequested args.Reason ] }

        sealAndAppendCancellation store state args nextRevision batch

    let private cancelAfterAdmission
        (store: IEventStore)
        (state: InquiryState)
        (args: CancelArgs)
        (fingerprint: string)
        (admitted: Result<IdempotencyOutcome<InquiryCommand>, CommandError>)
        : Task<obj> =
        match admitted with
        | Error fault -> refused (commandRefusal fault)
        | Ok(IdempotencyOutcome.Conflict message) ->
            refused
                { Code = "COMMAND_CONFLICT"
                  Path = "commandId"
                  Message = message }
        | Ok(IdempotencyOutcome.Replay revision) ->
            Task.FromResult(
                Encode.record
                    [ ("apiVersion", box Contract.apiVersion)
                      ("outcome", box "replayed")
                      ("inquiryId", box args.InquiryId)
                      ("revision", Encode.revision revision)
                      ("status", box (Encode.statusOf state)) ]
            )
        | Ok(IdempotencyOutcome.Fresh _) -> cancelFreshAdmission store state args fingerprint

    /// Requests cancellation. The request is appended through the same canonical fold
    /// the reader uses, so a retried command id returns the original receipt instead
    /// of appending twice. The reported status stays cancelling.
    let private cancelResult (store: IEventStore) (args: CancelArgs) : Task<obj> =
        task {
            match currentState store (InquiryId.create args.InquiryId) with
            | Error fault -> return! refused (currentRefusal fault)
            | Ok None -> return! refused (unknownInquiry args.InquiryId)
            | Ok(Some state) ->
                let fingerprint =
                    HostDigest.sha256Hex (
                        CanonicalJson.canonicalJson (
                            box
                                {| tool = Contract.toolName SphinxTool.InquiryCancel
                                   inquiryId = args.InquiryId
                                   reason = args.Reason |}
                        )
                    )

                let admitted =
                    Admission.admitCommand state args.CommandId fingerprint (InquiryCommand.CancelCommand args.Reason)

                return! cancelAfterAdmission store state args fingerprint admitted
        }

    /// Registers the seven public tools. Each one decodes its own arguments and then
    /// goes to the one Runtime; none of them decides what comes next.
    let private registerTools (server: obj) (store: IEventStore) : unit =
        let register (tool: SphinxTool) (description: string) (inputSchema: obj) (handler: obj -> Task<obj>) =
            let config =
                createObj
                    [ "name" ==> Contract.toolName tool
                      "description" ==> description
                      "inputSchema" ==> zObjectPassthrough zod inputSchema ]

            registerTool server (Contract.toolName tool) config (unaryHandler handler)
            |> ignore

        /// A tool whose arguments are decoded and then refused: the caller learns its
        /// arguments were read, and learns why the Runtime cannot yet carry the effect.
        let refusedAfterDecoding
            (tool: SphinxTool)
            (refusal: ToolRefusal)
            (decode: obj -> Result<'args, ToolRefusal>)
            (args: obj)
            : Task<obj> =
            match decode args with
            | Error decoded -> refused decoded
            | Ok _ -> refused refusal

        let startSchema =
            createObj
                [ "commandId"
                  ==> zString zod "Idempotent command identity; a repeat returns the original receipt"
                  "goalText" ==> zString zod "The user's goal text, stored byte-exact"
                  "constraints"
                  ==> zList zod (zString zod "one user constraint") "Supplementary constraints the user supplied"
                  "materialRefs"
                  ==> zList zod (zString zod "one material ref") "Content refs of material the user attached"
                  "authorizationRef"
                  ==> zString zod "Reference proving the user supplied this goal"
                  "profileRef" ==> zString zod "The declared profile this inquiry runs under" ]

        let workNextSchema =
            createObj
                [ "commandId"
                  ==> zString zod "Idempotent command identity; a repeat returns the original receipt"
                  "inquiryId" ==> zString zod "The inquiry whose ready work is claimed"
                  "limit" ==> zCount zod "Maximum number of work items to claim" ]

        let workSubmitSchema =
            createObj
                [ "commandId"
                  ==> zString zod "Idempotent command identity; a repeat returns the original receipt"
                  "inquiryId" ==> zString zod "The inquiry that owns the work"
                  "workId" ==> zString zod "The work item this answer belongs to"
                  "attempt" ==> zCount zod "The attempt this answer belongs to"
                  "fence" ==> zString zod "The logical fence of that attempt"
                  "canonicalResult"
                  ==> zString zod "The worker's canonical answer bytes, kept exactly as returned"
                  "resultSchema"
                  ==> zSchemaRef zod (zString zod "Schema identity") (zString zod "Schema content hash")
                  "clusterId" ==> zString zod "Ballot cluster this answer belongs to" ]

        let statusSchema = createObj [ "inquiryId" ==> zString zod "The inquiry to read" ]

        let cancelSchema =
            createObj
                [ "commandId"
                  ==> zString zod "Idempotent command identity; a repeat returns the original receipt"
                  "inquiryId" ==> zString zod "The inquiry to stop"
                  "reason" ==> zString zod "Why the caller asked to stop" ]

        let exportSchema =
            createObj
                [ "inquiryId" ==> zString zod "The inquiry to export"
                  "mode"
                  ==> zString zod "summary redacts; full declares what the durable store makes replayable" ]

        let goalAmendSchema =
            createObj
                [ "commandId"
                  ==> zString zod "Idempotent command identity; a repeat returns the original receipt"
                  "inquiryId" ==> zString zod "The inquiry whose goal is amended"
                  "authorizedBy"
                  ==> zString zod "The user authorization reference for this amendment"
                  "expectedRevision"
                  ==> zString zod "The inquiry revision this amendment is preconditioned on"
                  "addedConstraints"
                  ==> zList zod (zString zod "one added constraint") "Constraints the user added"
                  "replacementText"
                  ==> zOptionalString zod "Replacement goal text, when the user reworded it" ]

        register
            SphinxTool.InquiryStart
            "Starts a v2 inquiry and advances it. Refused for now: the start driver is not wired to durable creation and planning. The call changes nothing."
            startSchema
            (refusedAfterDecoding
                SphinxTool.InquiryStart
                (Tool.unsupported (Contract.toolName SphinxTool.InquiryStart))
                Tool.decodeStart)

        register
            SphinxTool.WorkNext
            "Claims up to limit ready work for the caller. Refused for now: the claim driver is not wired to durable work leasing. The call changes nothing and creates no lease."
            workNextSchema
            (refusedAfterDecoding
                SphinxTool.WorkNext
                (Tool.unsupported (Contract.toolName SphinxTool.WorkNext))
                Tool.decodeWorkNext)

        register
            SphinxTool.WorkSubmit
            "Submits the answer for the work the caller holds, bound to workId, attempt and fence. It carries the answer bytes and their schema and nothing else: a certificate patch, budget debit, event write or goal revision in the same call is refused by name. Refused for now: the submit driver is not wired to durable result admission and interpretation."
            workSubmitSchema
            (refusedAfterDecoding
                SphinxTool.WorkSubmit
                (Tool.unsupported (Contract.toolName SphinxTool.WorkSubmit))
                Tool.decodeWorkSubmit)

        register
            SphinxTool.InquiryStatus
            "Reads one inquiry: business status, revision, the Runtime's own next-step classification and the semantic view. It creates no lease, calls no model and changes nothing. An inquiry absent from the durable record is refused, never reported as an empty inquiry."
            statusSchema
            (fun args ->
                match Tool.decodeStatus args with
                | Error refusal -> refused refusal
                | Ok decoded -> statusResult store decoded)

        register
            SphinxTool.InquiryCancel
            "Requests cancellation of one inquiry through the same canonical fold the reader uses. An unconfirmed abort stays cancelling and never reports cancelled. Idempotent by commandId: a repeated commandId returns the original revision without appending again."
            cancelSchema
            (fun args ->
                match Tool.decodeCancel args with
                | Error refusal -> refused refusal
                | Ok decoded -> cancelResult store decoded)

        register
            SphinxTool.InquiryExport
            "Exports one inquiry. Refused for now: an export bundle must state a trace hash over the accepted envelopes, and this adapter reads published state only, so it cannot enumerate them. Read-only either way: no lease, no model call, no state change."
            exportSchema
            (refusedAfterDecoding
                SphinxTool.InquiryExport
                (Tool.traceUnavailable (Contract.toolName SphinxTool.InquiryExport))
                Tool.decodeExport)

        register
            SphinxTool.GoalAmend
            "Amends the goal with an explicit user authorizer. Refused for now: the amendment driver is not wired to durable user-authorized changes. The call changes nothing."
            goalAmendSchema
            (refusedAfterDecoding
                SphinxTool.GoalAmend
                (Tool.unsupported (Contract.toolName SphinxTool.GoalAmend))
                Tool.decodeGoalAmend)

    /// Boots the server against a durable store. The store already carries the v2 rule
    /// program, so replay happens through the same fold the caller reads.
    let serve (store: IEventStore) : JS.Promise<unit> =
        let server =
            construct
                mcpServerConstructor
                (createObj [ "name" ==> "sphinx"; "version" ==> Contract.apiVersion ])
                (createObj [])

        registerTools server store
        connect server (constructEmpty stdioTransportConstructor)

    /// Starts a stdio server, reporting a boot failure through stderr and exit code.
    let boot (commonDir: string) : JS.Promise<unit> =
        match Bind.createDurableStore commonDir (System.Guid.NewGuid().ToString("N")) with
        | Ok store -> serve store
        | Error reason ->
            consoleError (sprintf "[sphinx-mcp] durable boot failed: %s" reason)
            resolved
