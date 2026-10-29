namespace Wanxiangshu.Execution.Tool

open System
open Wanxiangshu.Foundation
open Wanxiangshu.Foundation.Identity
open Wanxiangshu.Host.Ports
open Wanxiangshu.Repository.Knowledge.Casebook

module ToolAdmissionTable =

    /// The one attachment fact: the Bookkeeper's transaction is attached to
    /// this session. The admission reads it through the real invocation's
    /// session; the surface answers it by session id.
    let bookkeeperAttached (sessionId: string) : bool =
        not (String.IsNullOrWhiteSpace sessionId)
        && BookkeeperRuntime.isAttached sessionId

    /// capability-enforcement-006: the Bookkeeper's own tool is admitted by
    /// owner-held attachment evidence, because its session is HostInternal and
    /// holds no public authority profile at all. The decision reads the real
    /// invocation's session, never a probe context.
    let bookkeeper: ToolAdmission =
        ToolAdmission.PrivateAttachment(fun invocation -> bookkeeperAttached (SessionId.value invocation.Session))

    let private staticAdmissions () : (string * ToolAdmission) list =
        [ "join", ToolAdmissions.join
          "horizon", ToolAdmissions.horizon
          "fission", ToolAdmissions.fission
          "review", ToolAdmissions.review
          "bash-honeypot", ToolAdmissions.bashHoneypot
          "assume", ToolAdmissions.assume
          "negotiate", ToolAdmissions.negotiate
          "askPreviousScope", ToolAdmissions.askPreviousScope
          "fetch", ToolAdmissions.fetch
          "fork", ToolAdmissions.fork
          "resume", ToolAdmissions.resume
          "commission", ToolAdmissions.commission
          "todowrite-for-bookkeeper", bookkeeper ]

    let private tryAdmissionFor (specName: string) : ToolAdmission option =
        match staticAdmissions () |> List.tryFind (fun (name, _) -> name = specName) with
        | Some(_, admission) -> Some admission

        | None when specName.StartsWith "todowrite-for-" && specName <> "todowrite-for-bookkeeper" ->
            Some(ToolAdmissions.programmingFor (specName.Substring 14))
        | None -> None

    /// capability-enforcement-006: the authority the execute gate resolves for a tool, so a
    /// consumer can tell an office tool from an internal leaf without guessing
    /// from the tool name.
    let tryAdmission (specName: string) : ToolAdmission option = tryAdmissionFor specName

    /// capability-enforcement-006: the internal-leaf decision for a session that holds no public
    /// office profile at all. An office tool is never admitted this way.
    let privateAttachmentAdmits (specName: string) (invocation: ToolInvocation) : bool =
        match tryAdmissionFor specName with
        | Some(ToolAdmission.PrivateAttachment predicate) -> predicate invocation
        | Some(ToolAdmission.OfficeRole _)
        | None -> false

    /// The internal-leaf decision asked by session id alone, for a surface
    /// that holds no call fence. It answers the same attachment fact the
    /// invocation-based decision reads.
    let privateAttachmentAdmitsForSession (specName: string) (sessionId: string) : bool =
        if specName = "todowrite-for-bookkeeper" then
            bookkeeperAttached sessionId
        else
            false

    /// AGENT-007 role gate, delegates to owner-defined tool admissions.
    /// The role reaches the product predicate as the neutral AgentRole the
    /// product projected from the established authority profile.
    let officeRolePredicate (specName: string) (role: AgentRole) : bool =
        // capability-enforcement-006: an internal leaf tool is admitted by attachment, never by a
        // public office, so no public Role may ever see it on this surface.
        match tryAdmissionFor specName with
        | Some(ToolAdmission.OfficeRole predicate) -> predicate role
        | Some(ToolAdmission.PrivateAttachment _)
        | None -> false
