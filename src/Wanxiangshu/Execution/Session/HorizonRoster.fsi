namespace Wanxiangshu.Execution.Session

open System.Threading.Tasks
open Wanxiangshu.Execution.Delegation.Fork
open Wanxiangshu.Foundation
open Wanxiangshu.Host.Domain
open Wanxiangshu.Participant.Provider

/// horizon(): who stands at the caller's horizon, as prose or as data.
/// Host-neutral; a host reaches it through `DelegationEnvironment`.
///
/// Every row is read from the durable handle projection through the same cut
/// join uses, so the two tools cannot disagree: a row says `returned`,
/// `did-not-return` or `voided` exactly when join would hand that consequence
/// over, `still-away` exactly when join would wait for a running charge, and
/// nothing a join already consumed reappears.
module HorizonRoster =

    [<RequireQualifiedAccess>]
    module Path =
        [<Literal>]
        val Description: string = "tool/horizon/description"

        [<Literal>]
        val Returned: string = "tool/horizon/returned"

        [<Literal>]
        val StillAway: string = "tool/horizon/still-away"

        [<Literal>]
        val Stalled: string = "tool/horizon/stalled"

        [<Literal>]
        val Idle: string = "tool/horizon/idle"

        [<Literal>]
        val DidNotReturn: string = "tool/horizon/did-not-return"

        [<Literal>]
        val Voided: string = "tool/horizon/voided"

        [<Literal>]
        val RemainsOpen: string = "tool/horizon/remains-open"

        [<Literal>]
        val Someone: string = "tool/horizon/someone"

        [<Literal>]
        val TerminalLabel: string = "tool/horizon/terminal-label"

        [<Literal>]
        val EmptyRoster: string = "tool/horizon/empty-roster"

        [<Literal>]
        val UnavailableFromContext: string = "tool/horizon/unavailable-from-context"

        [<Literal>]
        val CannotBeSeen: string = "tool/horizon/cannot-be-seen"

    /// Where one participant stands. Exactly one per row; a participant may
    /// carry several rows (an undelivered consequence beside a running charge).
    [<RequireQualifiedAccess>]
    type Standing =
        /// A charge is running in this process; join would wait for it.
        | StillAway
        /// A charge is open but nothing runs it; join would not wait for it.
        | Stalled
        /// A completion awaits join.
        | Returned
        /// An abandonment (cancelled, deadline, host gone) awaits join.
        | DidNotReturn
        /// A void awaits join: settled, never completed.
        | Voided
        /// The fixed DevOps binding with no charge and nothing to report.
        | Idle

    /// The four calibration numbers of a running charge; present only when
    /// both the delegator's estimate and the child's own start are readable.
    type Pace =
        { TurnsUsed: int
          TurnsLeft: int
          TimeUsedMs: float
          TimeLeftMs: float }

    type Presence =
        { Label: string
          Standing: Standing
          Pace: Pace option }

    /// The roster as data: participants, then the open terminals' names
    /// (empty when the runtime knows none).
    type Roster =
        { Participants: Presence list
          Terminals: string list }

    /// The wire word of a standing for `this.horizon()`.
    val wireStatus: standing: Standing -> string

    /// Whether an office holding this role may call horizon.
    val permits: role: Role -> bool

    /// The roster as instruction lines in the caller's language; a horizon
    /// the caller cannot be shown answers with one line saying so.
    val instructions: host: DelegationEnvironment -> Task<string list>

    /// The same roster as data. None when the caller cannot be shown a horizon.
    val roster: language: ProviderLanguage -> host: DelegationEnvironment -> Task<Roster option>
