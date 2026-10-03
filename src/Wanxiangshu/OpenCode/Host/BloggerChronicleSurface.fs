namespace Wanxiangshu.OpenCode

open System
open System.Threading.Tasks
open Fable.Core.JsInterop
open Wanxiangshu.Composition.Durable
open Wanxiangshu.Composition.Durable.Fact
open Wanxiangshu.Context.Companion
open Wanxiangshu.Execution.Session
open Wanxiangshu.Foundation
open Wanxiangshu.Foundation.Identity
open Wanxiangshu.Participant.Provider
open Wanxiangshu.Persistence.Journal

/// Blogger chronicle text injection owner surface (COGNITIVE-ENVIRONMENT-015).
/// The transform owns its model gate, journal read and placement invariants;
/// JS tests reach the real production entry and observe only JSON results.
module BloggerChronicleSurface =

    type private JournalHandleBox(handle: JournalHandle) =
        member _.Value = handle

    let private journalHandleOf (value: obj) = (unbox<JournalHandleBox> value).Value

    let private agentJournalOf (value: obj) = (journalHandleOf value).Journal

    let private isNullish (value: obj) =
        isNull value || emitJsExpr value "$0 === undefined"

    let private textOf (value: obj) =
        if isNull value then "" else string value

    let private languageOf (value: obj) : ProviderLanguage =
        match textOf value with
        | "zh-CN" -> ProviderLanguage.SimplifiedChinese
        | "en" -> ProviderLanguage.English
        | other -> invalidArg "language" (sprintf "unknown provider language: %s" other)

    /// Boot the production EventStore journal behind one opaque capability.
    let createJournal (directory: string) : Task<obj> =
        task {
            let! result =
                JournalSurface.boot directory "blogger-chronicle-surface" 0 (DateTimeOffset.UtcNow.ToString("O"))

            if isNullish result?ok || not (unbox<bool> result?ok) then
                return result
            else
                let handle = unbox<JournalHandle> result?journal

                return
                    box
                        {| ok = true
                           journal = (JournalHandleBox handle :> obj) |}
        }

    let disposeJournal (journal: obj) : unit =
        JournalSurface.dispose (journalHandleOf journal)

    /// Append one durable companion-link fact; the owner projection decides
    /// companion eligibility exactly as the production transform reads it.
    let appendCompanionLink (journal: obj) (payload: obj) : Task<obj> =
        task {
            if isNullish journal then
                return
                    box
                        {| ok = false
                           error = "journal required" |}
            else
                let mainSessionId = SessionId.create (textOf payload?session)
                let bloggerSessionId = SessionId.create (textOf payload?bloggerSession)

                let fact =
                    AgentFact.Companion(
                        CompanionFactCases.CompanionBloggerLinked
                            {| SessionId = mainSessionId
                               BloggerSessionId = bloggerSessionId
                               BloggerAgent = textOf payload?bloggerAgent |}
                    )

                let! result =
                    AgentJournal.appendAgent (StreamId.Session mainSessionId) None fact (agentJournalOf journal)

                return
                    match result with
                    | Ok projection ->
                        let companion =
                            SessionAssociationProjection.isCompanion
                                bloggerSessionId
                                projection.AgentProjections.Associations

                        box {| ok = true; companion = companion |}
                    | Error failure ->
                        box
                            {| ok = false
                               error = JournalAppendFailure.describe failure |}
        }

    /// Run the real production injection entry in place; the caller observes
    /// only the resulting message array.
    let maybeInject (journal: obj) (session: string) (language: obj) (outObj: obj) : obj =
        BloggerChronicleText.maybeInject (Some(agentJournalOf journal)) (Some session) (languageOf language) outObj

        let messages: obj array =
            if isNull outObj || isNull outObj?messages then
                [||]
            else
                unbox<obj array> outObj?messages

        box {| messages = messages |}
