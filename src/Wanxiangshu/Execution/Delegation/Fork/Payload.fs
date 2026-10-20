namespace Wanxiangshu.Execution.Delegation.Fork

open Wanxiangshu.Foundation

/// What a newly forked child is told, before it is rendered (ARCH-010, N3).
///
/// Typed rather than pre-composed text, because ARCH-010 requires the instruction/data split to be
/// decided by the producer and not inferred by a renderer: `Assignment` is the task, the other two
/// are context the child may read but must not mistake for the task.
/// DSL-state-combination: domain — assignment and optional context/payload are
/// one immutable fork request; absence means no supplied evidence, never a next
/// action or lifecycle latch.
type ForkChildAssignment =
    {
        /// The manager's request. Always present: a fork with no assignment is not a fork.
        Assignment: string
        /// Commissioner background for the child. Absent for a commissioner that has produced none yet.
        CommissionerRecord: string option
        /// Another person's bounded work statement, visible only as background context.
        Attachment: string option
        /// Authoritative scope: the HumanRoot prompts received since the previous assessment.
        RootRequirements: string list
        /// ARCH-010: machine-readable data that the child may read but must not mistake for the task.
        Payload: string option
    }

/// Already-localized fork-child instruction fragments (PROMPT-019).
type ForkChildInstructions =
    { Base: string list
      CommissionerRecord: string
      Attachment: string
      Requirements: string }

/// The first prompt of a forked child, as one ARCH-010 payload.
[<RequireQualifiedAccess>]
module ForkChildPayload =

    let BasePath = "delegation/fork-child-base"
    let CommissionerRecordPath = "delegation/fork-child-commissioner-record"
    let AttachmentPath = "delegation/fork-child-attachment"
    let RequirementsPath = "delegation/fork-child-requirements"

    let document (prose: ForkChildInstructions) (input: ForkChildAssignment) : LlmFacing.Document =
        let requirements =
            input.RootRequirements
            |> List.filter (fun text -> not (System.String.IsNullOrWhiteSpace text))

        let commissionerRecord =
            input.CommissionerRecord
            |> Option.map (fun record -> record.Trim())
            |> Option.filter (fun record -> record <> "")

        let attachment =
            input.Attachment
            |> Option.map (fun record -> record.Trim())
            |> Option.filter (fun record -> record <> "")

        let assignmentText = input.Assignment.Trim()

        let instructions =
            (if System.String.IsNullOrWhiteSpace assignmentText then
                 []
             else
                 [ assignmentText ])
            @ prose.Base
            @ (match commissionerRecord with
               | Some _ -> [ prose.CommissionerRecord ]
               | None -> [])
            @ (match attachment with
               | Some _ -> [ prose.Attachment ]
               | None -> [])
            @ (if List.isEmpty requirements then
                   []
               else
                   prose.Requirements :: requirements)

        let body =
            (match input.Payload with
             | Some payload when not (System.String.IsNullOrWhiteSpace payload) ->
                 [ LlmFacing.Data.stringField "content" payload ]
             | _ -> [])
            // Commissioner record / attachment view as ARCH-010 data fields (delegation-019/021).
            // Parent → child only: instruction header names the field.
            // Child → parent join is the opposite plane (the delivered view via
            // the JoinResultRenderer instruction plane) — do not conflate.
            @ (match commissionerRecord with
               | Some record -> [ LlmFacing.Data.stringField "commissioner_record" record ]
               | None -> [])
            @ (match attachment with
               | Some record -> [ LlmFacing.Data.stringField "attached_view" record ]
               | None -> [])

        LlmFacing.instructions instructions |> LlmFacing.withData body

    let render (prose: ForkChildInstructions) (input: ForkChildAssignment) : string =
        document prose input |> LlmFacing.render

    /// The positional form, for a call site that reads better without a record literal.
    let relay
        (prose: ForkChildInstructions)
        (assignment: string)
        (commissionerRecord: string option)
        (attachment: string option)
        (requirements: string list)
        (payload: string option)
        : string =
        render
            prose
            { Assignment = assignment
              CommissionerRecord = commissionerRecord
              Attachment = attachment
              RootRequirements = requirements
              Payload = payload }

    let relayDocument
        (prose: ForkChildInstructions)
        (assignment: string)
        (commissionerRecord: string option)
        (attachment: string option)
        (requirements: string list)
        (payload: string option)
        : LlmFacing.Document =
        document
            prose
            { Assignment = assignment
              CommissionerRecord = commissionerRecord
              Attachment = attachment
              RootRequirements = requirements
              Payload = payload }
