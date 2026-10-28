namespace Wanxiangshu.Execution.Session

open System
open System.Threading.Tasks
open Wanxiangshu.Composition.Durable
open Wanxiangshu.Execution.Delegation
open Wanxiangshu.Execution.Delegation.Fork.Host
open Wanxiangshu.Execution.Delegation.Fork
open Wanxiangshu.Execution.Delegation.Handle
open Wanxiangshu.Foundation
open Wanxiangshu.Foundation.Identity
open Wanxiangshu.Host.Domain
open Wanxiangshu.Participant.ManagedAgents
open Wanxiangshu.Participant.Provider
open Wanxiangshu.Persistence.Journal

/// horizon(): who stands at the caller's horizon, as prose or as data.
/// Host-neutral; a host reaches it through `DelegationEnvironment`.
module HorizonRoster =

    [<RequireQualifiedAccess>]
    module Path =
        [<Literal>]
        let Description = "tool/horizon/description"

        [<Literal>]
        let Returned = "tool/horizon/returned"

        [<Literal>]
        let StillAway = "tool/horizon/still-away"

        [<Literal>]
        let Stalled = "tool/horizon/stalled"

        [<Literal>]
        let Idle = "tool/horizon/idle"

        [<Literal>]
        let DidNotReturn = "tool/horizon/did-not-return"

        [<Literal>]
        let Voided = "tool/horizon/voided"

        [<Literal>]
        let RemainsOpen = "tool/horizon/remains-open"

        [<Literal>]
        let Someone = "tool/horizon/someone"

        [<Literal>]
        let TerminalLabel = "tool/horizon/terminal-label"

        [<Literal>]
        let EmptyRoster = "tool/horizon/empty-roster"

        [<Literal>]
        let UnavailableFromContext = "tool/horizon/unavailable-from-context"

        [<Literal>]
        let CannotBeSeen = "tool/horizon/cannot-be-seen"

    [<RequireQualifiedAccess>]
    type Standing =
        | StillAway
        | Stalled
        | Returned
        | DidNotReturn
        | Voided
        | Idle

    type Pace =
        { TurnsUsed: int
          TurnsLeft: int
          TimeUsedMs: float
          TimeLeftMs: float }

    type Presence =
        { Label: string
          Standing: Standing
          Pace: Pace option }

    type Roster =
        { Participants: Presence list
          Terminals: string list }

    let wireStatus standing =
        match standing with
        | Standing.StillAway -> "still-away"
        | Standing.Stalled -> "stalled"
        | Standing.Returned -> "returned"
        | Standing.DidNotReturn -> "did-not-return"
        | Standing.Voided -> "voided"
        | Standing.Idle -> "idle"

    let private prosePath standing =
        match standing with
        | Standing.StillAway -> Path.StillAway
        | Standing.Stalled -> Path.Stalled
        | Standing.Returned -> Path.Returned
        | Standing.DidNotReturn -> Path.DidNotReturn
        | Standing.Voided -> Path.Voided
        | Standing.Idle -> Path.Idle

    /// The calling session's id text; empty when the host could not name it.
    let private callerText (host: DelegationEnvironment) =
        match host.Caller.Session with
        | Some session -> SessionId.value session
        | None -> ""

    let private labelFor language (handle: HandleRecord) =
        if not (String.IsNullOrWhiteSpace handle.Byname) then
            handle.Byname.Trim()
        elif String.IsNullOrWhiteSpace handle.TargetAgent then
            ProviderProse.render language Path.Someone Map.empty
        else
            ManagedAgent.tryParse handle.TargetAgent
            |> Option.map (fun managed -> managed.Name)
            |> Option.defaultValue (handle.TargetAgent.Trim())

    /// The standing of one consequence join would hand over. Join's candidate
    /// list holds only completions awaiting join and abandonments.
    let private consequenceStanding (record: HandleRecord) =
        match record.Lifecycle with
        | HandleLifecycle.CompletedAwaitingJoin _ -> Some Standing.Returned
        | HandleLifecycle.Abandoned HandleAbandonReason.Voided -> Some Standing.Voided
        | HandleLifecycle.Abandoned _ -> Some Standing.DidNotReturn
        | HandleLifecycle.Active
        | HandleLifecycle.Retired -> None

    /// The fixed DevOps binding at rest: runtime-bound, never forked, with no
    /// charge of its own. It stays in sight so the caller knows it can resume it.
    let private isFixedDevOpsBinding (record: HandleRecord) =
        record.Work.IsNone
        && record.CanonicalRole = Role.DevOps
        && record.Byname = "devops"
        && record.TargetAgent = "devops"
        && record.Lifecycle = HandleLifecycle.Active
        && record.Ownership = HandleOwnership.DurableParentHandle

    /// The four numbers of a running charge, when both the delegator's
    /// estimate and the child's own start are known. Absent, never guessed.
    let private paceOf (now: DateTimeOffset) (journal: AgentJournal) (handle: HandleRecord) : Pace option =
        let estimate =
            AgentJournalPortAdapter.forDelegatedToolEstimate journal
            |> fun port -> DelegatedToolEstimateLedger.tryState port handle.ChildSessionId

        let startedAt =
            (AgentJournalPortAdapter.forSessionStartedAt journal).TryStartedAt handle.ChildSessionId

        match estimate, startedAt with
        | Some state, Some bound ->
            let turnsUsed = DelegatedToolEstimateProjection.countedCallCount state
            let turnsLeft = DelegatedToolEstimateProjection.remaining state
            let timeUsedMs = (now - bound).TotalMilliseconds

            Some
                { TurnsUsed = turnsUsed
                  TurnsLeft = turnsLeft
                  TimeUsedMs = timeUsedMs
                  TimeLeftMs = timeUsedMs * float turnsLeft / float (max 1 turnsUsed) }
        | _ -> None

    /// One row before rendering: the handle it belongs to (for ordering), and
    /// the presence it states.
    type private Row =
        { Order: int
          Handle: HandleId
          Presence: Presence }

    /// The roster rows of one parent, in creation order, read through the cut
    /// join uses (`HostForkJoin.currentProcessHandle` and
    /// `JoinDrain.orderedCandidates`).
    let private rowsOf
        (language: ProviderLanguage)
        (journal: AgentJournal)
        (runtime: HostForkRuntime)
        (handles: AgentLinkageProjection)
        : Row list =
        let visible = HostForkJoin.currentProcessHandle runtime
        let now = runtime.Now()

        let row (record: HandleRecord) standing pace =
            { Order = record.CreationOrder
              Handle = record.Handle
              Presence =
                { Label = labelFor language record
                  Standing = standing
                  Pace = pace } }

        let consequences =
            JoinDrain.orderedCandidates handles
            |> List.filter visible
            |> List.choose (fun record -> consequenceStanding record |> Option.map (fun s -> row record s None))
            |> List.distinctBy (fun r -> r.Handle, r.Presence.Standing)

        let charges =
            HandleProjection.activeHandles handles
            |> List.filter visible
            |> List.map (fun record ->
                if HostForkJoin.hasPendingRun runtime record.Handle then
                    row record Standing.StillAway (paceOf now journal record)
                else
                    row record Standing.Stalled None)

        let seen = (consequences @ charges) |> List.map (fun r -> r.Handle) |> Set.ofList

        let idle =
            handles.Handles
            |> Map.toList
            |> List.map snd
            |> List.filter (fun binding ->
                isFixedDevOpsBinding binding
                && visible binding
                && not (Set.contains binding.Handle seen))
            |> List.map (fun binding -> row binding Standing.Idle None)

        // A participant's consequences come before its running charge: the
        // caller reads what already arrived first.
        (consequences @ charges @ idle)
        |> List.mapi (fun index r -> index, r)
        |> List.sortBy (fun (index, r) -> r.Order, index)
        |> List.map snd

    let private terminalName (record: PtyRecord) =
        if String.IsNullOrWhiteSpace record.Command then
            None
        else
            Some(record.Command.Trim())

    let private rosterWithJournal
        (language: ProviderLanguage)
        (host: DelegationEnvironment)
        (journal: AgentJournal)
        : Task<Roster option> =
        task {
            let parentSessionId = SessionId.create (callerText host) |> host.LogicalOwnerFor

            do! host.EnsureWorkDevOpsBound parentSessionId

            match host.ForkRuntime() with
            | Error _ -> return None
            | Ok runtime ->
                let _, ptys = runtime.List()

                let handles =
                    (AgentJournal.snapshot journal).AgentProjections
                    |> AgentProjection.tryFind parentSessionId
                    |> Option.bind (fun session -> session.Handles)
                    |> Option.defaultValue HandleProjection.empty

                return
                    Some
                        { Participants = rowsOf language journal runtime handles |> List.map (fun r -> r.Presence)
                          Terminals =
                            ptys
                            |> List.sortBy (fun record -> record.PtyId)
                            |> List.map (fun record -> terminalName record |> Option.defaultValue "") }
        }

    let roster (language: ProviderLanguage) (host: DelegationEnvironment) : Task<Roster option> =
        match host.Journal with
        | None -> Task.FromResult None
        | Some journal -> rosterWithJournal language host journal

    let private labeled language path label =
        ProviderProse.render language path (Map [ "label", label ])

    let private presenceLines language (presence: Presence) =
        let standing = labeled language (prosePath presence.Standing) presence.Label

        match presence.Pace with
        | None -> [ standing ]
        | Some pace ->
            [ standing
              HorizonCalibration.render
                  (ProviderLanguage.externalName language)
                  presence.Label
                  pace.TurnsUsed
                  pace.TurnsLeft
                  pace.TimeUsedMs ]

    let private terminalLine language (name: string) =
        let label =
            if String.IsNullOrWhiteSpace name then
                ProviderProse.render language Path.TerminalLabel Map.empty
            else
                name

        labeled language Path.RemainsOpen label

    let private rosterLines language (roster: Roster) =
        let lines =
            (roster.Participants |> List.collect (presenceLines language))
            @ (roster.Terminals |> List.map (terminalLine language))

        if List.isEmpty lines then
            ProviderProse.instructionLines language Path.EmptyRoster Map.empty
        else
            lines

    let private seenLines language (roster: Roster option) =
        roster
        |> Option.map (rosterLines language)
        |> Option.defaultWith (fun () -> [ ProviderProse.render language Path.CannotBeSeen Map.empty ])

    let instructions (host: DelegationEnvironment) : Task<string list> =
        let language = ProviderLanguageBinding.forSessionText (callerText host)

        match host.Journal with
        | None -> Task.FromResult [ ProviderProse.render language Path.UnavailableFromContext Map.empty ]
        | Some journal ->
            task {
                let! roster = rosterWithJournal language host journal
                return seenLines language roster
            }

    let permits (role: Role) =
        OfficeCapability.isAllowed role ToolPermission.Horizon
