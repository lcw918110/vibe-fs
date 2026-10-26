namespace Wanxiangshu.Interaction.Dispatch

open Wanxiangshu.Interaction.Admission
open Wanxiangshu.Foundation
open Wanxiangshu.Interaction.Authority
open Wanxiangshu.Persistence.Journal
open Wanxiangshu.Composition.Durable
open Wanxiangshu.Execution.Session.ChatExecution

[<RequireQualifiedAccess>]
module PromptIngress =

    let private tryActiveHumanRootAgent (proj: PromptAuthority.PromptAuthorityProjection) =
        match proj.ActiveLogicalRun with
        | Some activeRun when activeRun.AuthorityKind = PromptAuthority.RootAuthorityKind.HumanRoot ->
            Some activeRun.SelectedAgent
        | _ -> None

    let private enrichHostMessage authority (message: ChatAdmissionIntent.DecodedMessage) =
        let agentOpt =
            match message.ExplicitAgent, message.PromptKey with
            | None, None -> authority |> Option.bind tryActiveHumanRootAgent
            | _ -> None

        match agentOpt with
        | Some agent ->
            { message with
                ExplicitAgent = Some agent }
        | None -> message

    let private acceptedExecutionEvidence (journal: AgentJournal option) (message: ChatAdmissionIntent.DecodedMessage) =
        match journal, message.SessionId, message.PhysicalUserMessageId with
        | Some durable, Some sessionId, Some physicalId ->
            (AgentJournal.snapshot durable).AgentProjections.ChatExecutions
            |> ChatExecutionProjection.byKey
                { SessionId = sessionId
                  PhysicalUserMessageId = physicalId }
            |> Option.map _.acceptedEvidence
        | _ -> None

    let resolveDecision (journal: AgentJournal option) (message: ChatAdmissionIntent.DecodedMessage) =
        let authority =
            match journal, message.SessionId with
            | Some durable, Some sessionId ->
                let runtime = PromptDispatcher.forPrompts (PromptJournalAdapter.create durable)
                let projection = runtime.ProjectionFor sessionId
                Some projection
            | Some _, None -> Some PromptAuthority.empty
            | None, _ -> None

        // interaction-authority-009: When host message omits explicit agent,
        // Ingress boundary projects active participant to admit as HumanMessage continuation
        let accepted = acceptedExecutionEvidence journal message

        let enrichedMessage =
            if accepted.IsSome then
                message
            else
                enrichHostMessage authority message

        ChatAdmissionIntent.resolve
            enrichedMessage
            { Authority = authority
              AcceptedExecutionEvidence = accepted }
