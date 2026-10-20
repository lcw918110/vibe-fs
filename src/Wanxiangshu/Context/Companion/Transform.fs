namespace Wanxiangshu.Context.Companion

open System
open System.Collections.Generic
open System.Threading.Tasks
open Fable.Core.JsInterop
open Wanxiangshu.OpenCode.HostMessageProjection
open Wanxiangshu.Context.Trace
open Wanxiangshu.Execution.Session
open Wanxiangshu.Execution.Session.Attachment
open Wanxiangshu.Foundation
open Wanxiangshu.Foundation.Identity
open Wanxiangshu.OpenCode
open Wanxiangshu.Participant.Provider.Projection
open Wanxiangshu.Persistence.Journal

module CompanionTransform =

    let private ensureCompanion
        (companions: Dictionary<string, CompanionHost>)
        (gate: obj)
        (satellites: CompanionLeaseRuntime)
        (sessionPort: ISessionHostPort)
        (journal: AgentJournal option)
        (workspaceDirectory: string option)
        (sessionId: string)
        : CompanionHost =
        lock gate (fun () ->
            match companions.TryGetValue sessionId with
            | true, value -> value
            | false, _ ->
                let durable =
                    journal
                    |> Option.map (fun j -> AgentJournalCompanionPort j :> ICompanionDurablePort)

                let value =
                    new CompanionHost(
                        SessionId.create sessionId,
                        sessionPort,
                        ?durable = durable,
                        satelliteRuntime = satellites
                    )

                companions.[sessionId] <- value
                value)

    let private messageContainer message =
        if isNull message then null
        elif isNull message?info then message
        else message?info

    let private tryMessageSessionId message =
        let container = messageContainer message

        if isNull container || isNull container?sessionID then
            None
        else
            Some(unbox<string> container?sessionID)

    let private tryMessageRole message =
        let container = messageContainer message

        if isNull container || isNull container?agent then
            None
        else
            Some(unbox<string> container?agent)

    let private tryMessageContext message =
        if isNull message || isNull message?info then
            None
        else
            Some(tryMessageSessionId message, tryMessageRole message)

    let private transformNonSatellite
        (companions: Dictionary<string, CompanionHost>)
        (gate: obj)
        (satellites: CompanionLeaseRuntime)
        (sessionPort: ISessionHostPort)
        (journal: AgentJournal option)
        (workspaceDirectory: string option)
        (sessionId: string)
        (rawMessages: obj list)
        (rawOutObj: obj)
        : Task<unit> =
        task {
            let _ =
                ensureCompanion companions gate satellites sessionPort journal workspaceDirectory sessionId

            replaceMessagesInPlace rawOutObj rawMessages
        }

    let private processSession
        (companions: Dictionary<string, CompanionHost>)
        (gate: obj)
        (satellites: CompanionLeaseRuntime)
        (sessionPort: ISessionHostPort)
        (journal: AgentJournal option)
        (workspaceDirectory: string option)
        (sessionId: string)
        (rawMessages: obj list)
        (rawOutObj: obj)
        : Task<unit> =
        task {
            let isSatelliteSession =
                match journal with
                | None -> true
                | Some j ->
                    SessionAssociationProjection.isSatellite
                        (SessionId.create sessionId)
                        (AgentJournal.snapshot j).AgentProjections.Associations

            if not isSatelliteSession then
                return!
                    transformNonSatellite
                        companions
                        gate
                        satellites
                        sessionPort
                        journal
                        workspaceDirectory
                        sessionId
                        rawMessages
                        rawOutObj
        }

    /// Main-session transform: Host view unchanged.
    let handleCompanionTransform
        (companions: Dictionary<string, CompanionHost>)
        (gate: obj)
        (satellites: CompanionLeaseRuntime)
        (sessionPort: ISessionHostPort)
        (journal: AgentJournal option)
        (workspaceDirectory: string option)
        (inObj: obj)
        (rawOutObj: obj)
        : Task<unit> =
        task {
            let rawMessages = unbox<obj array> rawOutObj?messages |> Array.toList

            let messageContext = rawMessages |> List.tryPick tryMessageContext

            match messageContext with
            | Some(Some messageSessionId, _) when not (isNull inObj) && isNull inObj?sessionID ->
                inObj?sessionID <- messageSessionId
            | _ -> ()

            let sessionId =
                if isNull inObj || isNull inObj?sessionID then
                    ""
                else
                    unbox<string> inObj?sessionID

            if not (String.IsNullOrWhiteSpace sessionId) && not (isNull rawOutObj?messages) then
                return!
                    processSession
                        companions
                        gate
                        satellites
                        sessionPort
                        journal
                        workspaceDirectory
                        sessionId
                        rawMessages
                        rawOutObj
        }

    let applyCompanionForOrdinaryMaterial
        (companions: Dictionary<string, CompanionHost>)
        (gate: obj)
        (satellites: CompanionLeaseRuntime)
        (sessionPort: ISessionHostPort)
        (journal: AgentJournal option)
        (workspaceDirectory: string option)
        (projectionSessionIdOpt: string option)
        (inObj: obj)
        (outObj: obj)
        : Task<unit> =
        task {
            do! handleCompanionTransform companions gate satellites sessionPort journal workspaceDirectory inObj outObj
        }
