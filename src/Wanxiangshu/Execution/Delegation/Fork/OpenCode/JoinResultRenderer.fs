namespace Wanxiangshu.Execution.Delegation.Fork.OpenCode

open System
open Wanxiangshu.Change
open Wanxiangshu.Execution.Delegation.Fork
open Wanxiangshu.Execution.Session
open Wanxiangshu.Execution.Session.Wait
open Wanxiangshu.Foundation
open Wanxiangshu.Host.Contract
open Wanxiangshu.OpenCode
open Wanxiangshu.Participant.Provider

/// EXEC-004 / EXEC-017 / EXEC-030: LLM-facing join wire — natural language + delivered view only.
/// No status / count / ordinal / kind / agent / code / message DTO plane.
///
/// Directional delivered-view plane (delegation-013 / delegation-019 / provider-projection-009):
/// child → parent join MUST keep the delivered view in Instruction Plane. Do NOT
/// wrap it in `view = …` or any other TOML data field. Parent → child fork payload is the opposite contract
/// (`commissioner_record` / `attached_view` fields) — do not conflate.
module JoinResultRenderer =

    [<RequireQualifiedAccess>]
    module Path =
        [<Literal>]
        let AgentReturned = "tool/join/agent-returned"

        [<Literal>]
        let AgentFailed = "tool/join/agent-failed"

        [<Literal>]
        let AgentDidNotReturn = "tool/join/agent-did-not-return"

        [<Literal>]
        let PtyEnded = "tool/join/pty-ended"

        [<Literal>]
        let PtyInterrupted = "tool/join/pty-interrupted"

        [<Literal>]
        let TerminalLabel = "tool/join/terminal-label"

        [<Literal>]
        let InterruptOperatorAbort = "tool/join/interrupt-operator-abort"

        [<Literal>]
        let InterruptUserMessage = "tool/join/interrupt-user-message"

        [<Literal>]
        let InterruptDeadline = "tool/join/interrupt-deadline"

        [<Literal>]
        let OrchestratorPublished = "tool/join/orchestrator-published"

        [<Literal>]
        let OrchestratorRejectedDirty = "tool/join/orchestrator-rejected-dirty"

        [<Literal>]
        let OrchestratorIntegrationFailed = "tool/join/orchestrator-integration-failed"

        [<Literal>]
        let OrchestratorEmpty = "tool/join/orchestrator-empty"

        [<Literal>]
        let ForkNothingToJoin = "tool/join/fork-nothing-to-join"

        [<Literal>]
        let ForkCancelled = "tool/join/fork-cancelled"

        [<Literal>]
        let ForkJoinInProgress = "tool/join/fork-join-in-progress"

        [<Literal>]
        let ForkNotFound = "tool/join/fork-not-found"

        [<Literal>]
        let ForkTimedOut = "tool/join/fork-timed-out"

        [<Literal>]
        let ForkMaterializationFailed = "tool/join/fork-materialization-failed"

        [<Literal>]
        let RemainingCompletions = "tool/join/remaining-completions"

        [<Literal>]
        let Someone = "tool/join/someone"

    let private prose lang path subs = ProviderProse.render lang path subs

    let private bynameLine lang path name =
        prose lang path (Map [ "byname", name ])

    let private ensureInstruction (text: string) = text.Trim()

    let private entry (instructions: string list) (body: LlmFacing.DataBlock list) : LlmFacing.Document =
        LlmFacing.instructions (instructions |> List.map ensureInstruction)
        |> LlmFacing.withData body

    let private fallbackByname (agentId: string) (agentNameRaw: string) =
        match ManagedAgent.tryParse agentNameRaw with
        | Some agent -> agent.Name
        | None -> agentNameRaw

    let private byname
        (lang: ProviderLanguage)
        (resolveAgentName: string -> string)
        (agentId: string)
        (agentNameRaw: string)
        : string =
        let presented = resolveAgentName agentId

        if not (String.IsNullOrWhiteSpace presented) then
            presented.Trim()
        elif not (String.IsNullOrWhiteSpace agentNameRaw) then
            fallbackByname agentId agentNameRaw
        else
            prose lang Path.Someone Map.empty

    let private tryParseInt (value: string) : int option =
        match Int32.TryParse value with
        | true, code -> Some code
        | false, _ -> None

    let private tryParseExitCode (outcome: string) : int option =
        let trimmed = outcome.Trim()

        if String.IsNullOrWhiteSpace trimmed then
            None
        elif trimmed.StartsWith("exit ", StringComparison.OrdinalIgnoreCase) then
            trimmed.Substring(5).Trim() |> tryParseInt
        else
            tryParseInt trimmed

    let private outputText (outcome: string) (detail: string option) (exitCode: int option) =
        match detail, exitCode with
        | Some text, _ when not (String.IsNullOrWhiteSpace text) -> text
        | _, Some _ -> ""
        | _, None -> outcome

    let private terminalBody (outcome: string) (detail: string option) : LlmFacing.DataBlock list =
        let exitCode = tryParseExitCode outcome

        let fields =
            match exitCode with
            | Some code -> [ LlmFacing.Data.intField "exit_code" code ]
            | None -> []

        let output = outputText outcome detail exitCode

        if String.IsNullOrWhiteSpace output then
            fields
        else
            fields @ [ LlmFacing.Data.stringField "output" output ]

    let private terminalLabel lang (resolveTerminalLabel: string -> string) (ptyId: string) =
        let resolved = resolveTerminalLabel ptyId

        if String.IsNullOrWhiteSpace resolved then
            prose lang Path.TerminalLabel Map.empty
        else
            resolved.Trim()

    /// EXEC-017: interrupt consequences are natural language, not error DTO.
    let renderInterrupted (lang: ProviderLanguage) (reason: JoinInterruptReason) : string =
        let path =
            match reason with
            | JoinInterruptReason.OperatorAbort -> Path.InterruptOperatorAbort
            | JoinInterruptReason.UserMessageArrived -> Path.InterruptUserMessage
            | JoinInterruptReason.DeadlineExpired -> Path.InterruptDeadline

        entry [ prose lang path Map.empty ] [] |> LlmFacing.render

    let private renderAgentCompleted
        (lang: ProviderLanguage)
        (resolveAgentName: string -> string)
        (completion: RunCompletion)
        (payload: AgentCompletionPayload)
        : LlmFacing.Document =
        let name =
            byname lang resolveAgentName (AgentCompletion.agentId completion.Outcome) completion.AgentName

        let instructions =
            if String.IsNullOrWhiteSpace payload.View then
                [ bynameLine lang Path.AgentReturned name ]
            else
                [ bynameLine lang Path.AgentReturned name; payload.View ]

        entry instructions []

    let private renderAgentFailed
        (lang: ProviderLanguage)
        (resolveAgentName: string -> string)
        (completion: RunCompletion)
        (payload: AgentFailurePayload)
        : LlmFacing.Document =
        let name =
            byname lang resolveAgentName (AgentCompletion.agentId completion.Outcome) completion.AgentName

        let instructions =
            if String.IsNullOrWhiteSpace payload.Message then
                [ bynameLine lang Path.AgentFailed name ]
            else
                [ bynameLine lang Path.AgentFailed name; payload.Message ]

        entry instructions []

    let private renderAgentAbandoned
        (lang: ProviderLanguage)
        (resolveAgentName: string -> string)
        (agentId: string)
        (agentNameRaw: string)
        : LlmFacing.Document =
        let name = byname lang resolveAgentName agentId agentNameRaw
        entry [ bynameLine lang Path.AgentDidNotReturn name ] []

    let private renderPtyEnded
        (lang: ProviderLanguage)
        (resolveTerminalLabel: string -> string)
        (templatePath: string)
        (ptyId: string)
        (outcome: string)
        (detail: string option)
        : LlmFacing.Document =
        let label = terminalLabel lang resolveTerminalLabel ptyId
        entry [ prose lang templatePath (Map [ "label", label ]) ] (terminalBody outcome detail)

    let private renderAgentJoinItem
        (lang: ProviderLanguage)
        (resolveAgentName: string -> string)
        (completion: RunCompletion)
        (item: AgentJoinItem)
        : LlmFacing.Document =
        match item with
        | AgentCompletedItem payload -> renderAgentCompleted lang resolveAgentName completion payload
        | AgentFailedItem payload -> renderAgentFailed lang resolveAgentName completion payload
        | AgentAbandonedItem(agentId, _) -> renderAgentAbandoned lang resolveAgentName agentId completion.AgentName

    let private renderPtyJoinItem
        (lang: ProviderLanguage)
        (resolveTerminalLabel: string -> string)
        (item: PtyJoinItem)
        : LlmFacing.Document =
        match item with
        | PtyExited payload -> renderPtyEnded lang resolveTerminalLabel Path.PtyEnded payload.PtyId payload.Outcome None
        | PtyFailed payload ->
            renderPtyEnded lang resolveTerminalLabel Path.PtyEnded payload.PtyId payload.Outcome (Some payload.Message)
        | PtyAborted payload ->
            renderPtyEnded
                lang
                resolveTerminalLabel
                Path.PtyInterrupted
                payload.PtyId
                payload.Outcome
                (Some payload.Message)

    let private renderAgentItem
        (lang: ProviderLanguage)
        (resolveAgentName: string -> string)
        (agentItem: AgentJoinItem)
        : LlmFacing.Document =
        let nameStub =
            match agentItem with
            | AgentCompletedItem p ->
                { RunId = p.RunId
                  AgentName = ""
                  Role = p.Role
                  Outcome = AgentCompleted p
                  CompletedAt = DateTimeOffset.UtcNow }
            | AgentFailedItem p ->
                { RunId = p.RunId
                  AgentName = ""
                  Role = defaultArg p.Role Role.Distiller
                  Outcome = AgentFailed p
                  CompletedAt = DateTimeOffset.UtcNow }
            | AgentAbandonedItem(agentId, reason) ->
                { RunId = "abandoned-" + agentId
                  AgentName = ""
                  Role = Role.Distiller
                  Outcome = AgentAbandoned(agentId, reason)
                  CompletedAt = DateTimeOffset.UtcNow }

        renderAgentJoinItem lang resolveAgentName nameStub agentItem

    let private renderJoinItem
        (lang: ProviderLanguage)
        (resolveAgentName: string -> string)
        (resolveTerminalLabel: string -> string)
        (item: JoinItem)
        : LlmFacing.Document =
        match item with
        | AgentItem agentItem -> renderAgentItem lang resolveAgentName agentItem
        | PtyItem ptyItem -> renderPtyJoinItem lang resolveTerminalLabel ptyItem

    let private renderJoinItemDocs
        (lang: ProviderLanguage)
        (resolveAgentName: string -> string)
        (resolveTerminalLabel: string -> string)
        (items: JoinItem list)
        : LlmFacing.Document list =
        items |> List.map (renderJoinItem lang resolveAgentName resolveTerminalLabel)

    let private promptDoc (lang: ProviderLanguage) : LlmFacing.Document =
        entry [ prose lang Path.RemainingCompletions Map.empty ] []

    let private renderDocs (docs: LlmFacing.Document list) : string =
        LlmFacing.combine docs |> LlmFacing.render

    let private evaluateCandidateWindow (candidateText: string) : string option =
        match ToolResultBound.fitsInWindow candidateText with
        | true -> Some candidateText
        | false -> None

    let private tryStepWindowPrefix
        (itemDocs: LlmFacing.Document list)
        (prompt: LlmFacing.Document)
        (total: int)
        (k: int)
        : string option =
        match k >= total - 1 with
        | true -> None
        | false ->
            let nextCand = renderDocs ((itemDocs |> List.take (k + 1)) @ [ prompt ])
            evaluateCandidateWindow nextCand

    let private searchMaxWindowPrefix
        (itemDocs: LlmFacing.Document list)
        (prompt: LlmFacing.Document)
        (total: int)
        (firstCand: string)
        : int * string =
        let rec search k lastValid =
            match tryStepWindowPrefix itemDocs prompt total k with
            | Some nextCand -> search (k + 1) nextCand
            | None -> k, lastValid

        match ToolResultBound.fitsInWindow firstCand with
        | true -> search 1 firstCand
        | false -> 1, firstCand

    let private renderSingleItem
        (lang: ProviderLanguage)
        (resolveAgentName: string -> string)
        (resolveTerminalLabel: string -> string)
        (item: JoinItem)
        : string * JoinItem list =
        let doc = renderJoinItem lang resolveAgentName resolveTerminalLabel item
        LlmFacing.render doc, []

    let private renderMultiItems
        (lang: ProviderLanguage)
        (resolveAgentName: string -> string)
        (resolveTerminalLabel: string -> string)
        (items: JoinItem list)
        : string * JoinItem list =
        let total = List.length items
        let itemDocs = renderJoinItemDocs lang resolveAgentName resolveTerminalLabel items
        let allRendered = renderDocs itemDocs

        if ToolResultBound.fitsInWindow allRendered then
            allRendered, []
        else
            let prompt = promptDoc lang
            let firstCand = renderDocs [ itemDocs.[0]; prompt ]

            let deliveredCount, renderedWithPrompt =
                searchMaxWindowPrefix itemDocs prompt total firstCand

            let remaining = items |> List.skip deliveredCount
            renderedWithPrompt, remaining

    /// FIFO window-bounded render. Returns rendered text and un-rendered remaining items.
    let renderJoinItemBatchWithWindow
        (lang: ProviderLanguage)
        (resolveAgentName: string -> string)
        (batch: NonEmptyBatch<JoinItem>)
        (resolveTerminalLabel: string -> string)
        : string * JoinItem list =
        match NonEmptyBatch.toList batch with
        | [ single ] -> renderSingleItem lang resolveAgentName resolveTerminalLabel single
        | items -> renderMultiItems lang resolveAgentName resolveTerminalLabel items

    /// EXEC-004 / EXEC-018 / EXEC-020: JoinItem batch (production JoinTool path).
    let renderJoinItemBatch
        (lang: ProviderLanguage)
        (resolveAgentName: string -> string)
        (batch: NonEmptyBatch<JoinItem>)
        (resolveTerminalLabel: string -> string)
        : string =
        let rendered, _ =
            renderJoinItemBatchWithWindow lang resolveAgentName batch resolveTerminalLabel

        rendered

    let private orchestratorLine (lang: ProviderLanguage) (verdict: OrchestratorVerdict) : string =
        let path =
            match verdict with
            | OrchestratorVerdict.Published _ -> Path.OrchestratorPublished
            | OrchestratorVerdict.PublishedPendingCleanup _ -> Path.OrchestratorPublished
            | OrchestratorVerdict.Cancelled _ -> Path.OrchestratorEmpty
            | OrchestratorVerdict.RejectedDirty _ -> Path.OrchestratorRejectedDirty
            | OrchestratorVerdict.IntegrationFailed _ -> Path.OrchestratorIntegrationFailed
            | OrchestratorVerdict.Empty -> Path.OrchestratorEmpty

        prose lang path Map.empty

    /// EXEC-019: orchestrator verdict batch (FIFO; caller already capped at MaxJoinBatch).
    let renderOrchestratorBatch (lang: ProviderLanguage) (verdicts: NonEmptyBatch<OrchestratorVerdict>) : string =
        NonEmptyBatch.toList verdicts
        |> List.map (fun verdict -> entry [ orchestratorLine lang verdict ] [])
        |> LlmFacing.combine
        |> LlmFacing.render

    /// True ForkError path — natural language only (not user interrupt).
    let renderForkError (lang: ProviderLanguage) (error: ForkError) (resolveAgentName: string -> string) : string =
        let line =
            match error with
            | ForkError.NothingToJoin
            | ForkError.Empty -> prose lang Path.ForkNothingToJoin Map.empty
            | ForkError.Cancelled -> prose lang Path.ForkCancelled Map.empty
            | ForkError.JoinInProgress -> prose lang Path.ForkJoinInProgress Map.empty
            | ForkError.Abandoned(id, _) ->
                let name = byname lang resolveAgentName id ""
                bynameLine lang Path.AgentDidNotReturn name
            | ForkError.NotFound _ -> prose lang Path.ForkNotFound Map.empty
            | ForkError.TimedOut -> prose lang Path.ForkTimedOut Map.empty
            | ForkError.TerminalMaterializationFailed _ -> prose lang Path.ForkMaterializationFailed Map.empty

        entry [ line ] [] |> LlmFacing.render
