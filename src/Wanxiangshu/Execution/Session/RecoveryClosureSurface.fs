namespace Wanxiangshu.Execution.Session

open Fable.Core.JsInterop
open Wanxiangshu.Execution.Session.Recovery
open Wanxiangshu.Foundation.Identity

/// Plain member-list surface for the recovery dependency closure. Callers name the
/// discovered members as ordinary objects; the production dedupe and ordering stay
/// in RecoveryClosureProjection, so this surface adds no second implementation.
[<RequireQualifiedAccess>]
module RecoveryClosureSurface =
    let private text (value: obj) =
        if isNull value then "" else string value

    let private nodeOf (value: obj) : SessionRecovery.RecoveryNode =
        match text (value?kind) with
        | "child" ->
            SessionRecovery.RecoveryNode.AgentChild(
                SessionId.create (text (value?parent)),
                SessionId.create (text (value?child)),
                AgentHandleId.create (text (value?handle))
            )
        | "companion" ->
            SessionRecovery.RecoveryNode.Companion(
                SessionId.create (text (value?main)),
                SessionId.create (text (value?companion))
            )
        | "managerJob" ->
            SessionRecovery.RecoveryNode.ManagerJob(
                ManagerJobId.create (text (value?job)),
                SessionId.create (text (value?manager))
            )
        | "work" -> SessionRecovery.RecoveryNode.WorkSession(SessionId.create (text (value?session)))
        | unknown -> invalidArg "kind" ("unknown recovery node: " + unknown)

    let closureTokens (root: string) (candidates: obj array) (journalSequence: int) : string array =
        let members = candidates |> Array.toList |> List.map nodeOf

        RecoveryClosureProjection.closureOf (SessionId.create root) members (int64 journalSequence)
        |> fun closure -> closure.Nodes
        |> List.map SessionRecovery.RecoveryNode.token
        |> List.toArray
