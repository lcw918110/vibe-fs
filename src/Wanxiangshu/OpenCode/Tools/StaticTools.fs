namespace Wanxiangshu.OpenCode

open System
open Fable.Core.JsInterop
open Wanxiangshu.Ablation
open Wanxiangshu.Foundation


module StaticTools =

    /// One permission may expand to several provider verb names (Pty, Behavior, Exec).
    let toolNames (p: ToolPermission) : string list =
        match p with
        | ToolPermission.Fork -> [ "fork" ]
        | ToolPermission.Resume -> [ "resume" ]
        | ToolPermission.Join -> [ "join" ]
        | ToolPermission.Horizon -> [ "horizon" ]
        | ToolPermission.Fission -> [ "fission" ]
        | ToolPermission.Read -> [ "read" ]
        | ToolPermission.Write -> [ "write" ]
        | ToolPermission.Edit -> [ "edit" ]
        | ToolPermission.Glob -> [ "glob" ]
        | ToolPermission.Grep -> [ "grep" ]
        | ToolPermission.Move -> [ "mv" ]
        | ToolPermission.Remove -> [ "rm" ]
        | ToolPermission.BashHoneypot -> [ "bash-honeypot" ]
        | ToolPermission.Exec -> [ "run" ]
        | ToolPermission.Pty -> [ "open-terminal"; "send-terminal"; "read-terminal"; "signal-terminal" ]
        | ToolPermission.ReviewAssessment -> [ "review" ]
        | ToolPermission.Chronicle -> [ "chronicle" ]
        | ToolPermission.Fetch -> [ "fetch" ]
        | ToolPermission.Finality -> [ "suicide" ]
        | ToolPermission.JsPlan -> [ "js-plan" ]
        | ToolPermission.Ask -> [ "ask" ]
        | ToolPermission.Handoff -> [ "handoff" ]
        | ToolPermission.Deliver -> [ "deliver" ]

    /// Primary name for permissions with a single verb (tests / simple maps).
    let toolName (p: ToolPermission) =
        match toolNames p with
        | name :: _ -> name
        | [] -> invalidOp "ToolPermission must expand to at least one provider name"

    /// The complete constructor list of ToolPermission. It enumerates the
    /// catalog type for the reverse lookup; the name↔permission mapping itself
    /// stays in `toolNames`.
    let private allPermissions =
        [ ToolPermission.Fork
          ToolPermission.Resume
          ToolPermission.Join
          ToolPermission.Horizon
          ToolPermission.Fission
          ToolPermission.Read
          ToolPermission.Write
          ToolPermission.Edit
          ToolPermission.Glob
          ToolPermission.Grep
          ToolPermission.Move
          ToolPermission.Remove
          ToolPermission.Exec
          ToolPermission.Pty
          ToolPermission.ReviewAssessment
          ToolPermission.Chronicle
          ToolPermission.Fetch
          ToolPermission.Finality
          ToolPermission.BashHoneypot
          ToolPermission.JsPlan
          ToolPermission.Ask
          ToolPermission.Handoff
          ToolPermission.Deliver ]

    /// capability-enforcement-012: the sole reverse lookup for tool name →
    /// permission. The schema projection, the dispatch tool map and the
    /// execute gate all read this, so no consumer keeps a second
    /// name→permission table.
    let permissionOfToolName (name: string) : ToolPermission option =
        allPermissions
        |> List.tryFind (fun permission -> toolNames permission |> List.contains name)

    /// The office projection of one name: true only when the role's permission
    /// set owns the permission this exact name maps to.
    let admitsToolForRole (role: Role) (name: string) : bool =
        permissionOfToolName name
        |> Option.exists (fun permission -> OfficeCapability.permissions role |> Set.contains permission)


    /// JS-001: the generated js-ROLE tool name for a role.
    let jsToolName (role: Role) : string =
        "js-" + (string role).ToLowerInvariant()

    /// Shared office boundary for lightweight cognitive utilities. Both the
    /// custom assume tool and the Host-native todowrite surface consume this
    /// predicate so schema visibility and execution admission cannot drift.
    let cognitiveUtilityRoleAllowed (role: Role) : bool =
        role <> Role.Blogger && role <> Role.Distiller

    /// JS-001: a role whose capability set includes any filesystem permission
    /// gets its js-* tool allowed in the permission matrix.
    let private hasFsCapability (role: Role) : bool =
        let fsPermissions =
            set
                [ ToolPermission.Read
                  ToolPermission.Write
                  ToolPermission.Edit
                  ToolPermission.Glob
                  ToolPermission.Grep ]

        Set.intersect (OfficeCapability.permissions role) fsPermissions
        |> Set.isEmpty
        |> not

    /// Single source: OfficeCapability.permissions → OpenCode agent permission object.
    /// Emits explicit allow/deny for the full known tool name set so host schema
    /// filters and contract tests see concrete denies (not only "*").
    let knownToolNames =
        [ "fork"
          "resume"
          "commission"
          "open-terminal"
          "send-terminal"
          "read-terminal"
          "signal-terminal"
          "join"
          "horizon"
          "fission"
          "read"
          "write"
          "edit"
          "glob"
          "grep"
          "skill"
          "todowrite"
          "assume"
          "defer"
          "publish"
          "mv"
          "rm"
          "bash-honeypot"
          "run"
          "review"
          "chronicle"
          "fetch"
          "suicide"
          "js-engineer"
          "js-manager"
          "js-orchestrator"
          "js-devops"
          "js-blogger"
          "js-bookkeeper"
          "js-plan"
          "ask"
          "handoff"
          "deliver" ]

    /// PROMPT-012: an explicit complete allow/deny map for PromptInput.tools.
    /// The office half comes from the one reverse lookup; utility tools keep
    /// their own predicates.
    let requestToolMap (allowed: Set<ToolPermission>) : Map<string, bool> =
        let registry = AblationGate.registry ()

        knownToolNames
        |> List.map (fun name ->
            name,
            (name = "skill"
             || name = "todowrite"
             || name = "assume"
             || name = "defer"
             || name = "publish"
             || (permissionOfToolName name
                 |> Option.exists (fun permission -> Set.contains permission allowed))))
        |> Map.ofList
        |> AblationGate.filterToolPermissionMap registry

    let private defaultPermission (allowed: Set<ToolPermission>) name =
        let admitted =
            permissionOfToolName name
            |> Option.exists (fun permission -> Set.contains permission allowed)

        if admitted then "allow" else "deny"

    let private jsPermission role name =
        if name = "js-manager" && role = Role.Manager then
            "allow"
        elif name = "js-plan" && role = Role.Plan then
            "allow"
        elif name = jsToolName role && hasFsCapability role then
            "allow"
        else
            "deny"

    let private permissionFor (registry: AblationRegistry) allowed role name =
        match not (AblationGate.toolSchemaDenied registry name), name, role with
        | false, _, _ -> "deny"
        | true, ("ask" | "handoff" | "deliver" | "js-plan"), Role.Plan -> "allow"
        | true, "resume", Role.Plan -> "allow"
        | true, "fission", Role.Manager -> "deny"
        | true, "commission", Role.Manager -> "deny"
        | true, ("read" | "grep" | "glob"), Role.Manager -> "deny"
        | true, "fork", Role.Orchestrator -> "deny"
        | true, "resume", Role.Orchestrator -> "deny"
        | true, "commission", Role.Orchestrator -> "allow"
        | true, ("open-terminal" | "send-terminal" | "read-terminal" | "signal-terminal"), Role.DevOps -> "allow"
        | true, "fork", Role.DevOps -> "deny"
        | true, "resume", Role.DevOps -> "deny"
        | true, "run", Role.DevOps -> "allow"
        | true, "skill", Role.Blogger -> "deny"
        | true, "skill", _ -> "allow"
        | true, ("assume" | "todowrite"), role when not (cognitiveUtilityRoleAllowed role) -> "deny"
        | true, ("assume" | "todowrite"), _ -> "allow"
        | true, ("defer" | "publish"), Role.Blogger -> "deny"
        | true, ("defer" | "publish"), _ -> "allow"
        | true, "js-bookkeeper", _ -> "deny"
        | true, name, _ when name.StartsWith "js-" -> jsPermission role name
        | true, _, _ -> defaultPermission allowed name

    let permissionObj (role: Role) : obj =
        let registry = AblationGate.registry ()
        let allowed = OfficeCapability.permissions role

        // Host defaults set external_directory:* = ask (agent.ts). Rulesets merge by
        // flat concat + findLast, so this trailing allow cancels the Host ask and
        // stops permission.asked prompts on paths outside the project directory.
        let pairs =
            [ yield "*", box "deny"
              yield "external_directory", box "allow"
              for name in knownToolNames do
                  yield name, box (permissionFor registry allowed role name) ]

        createObj pairs

    /// OpenCode AgentConfig: mode + permission + optional system prompt.
    /// `prompt` is the host agent system prompt, never a user message body.
    let private primaryAgent (role: Role) (systemPrompt: string option) : obj =
        match systemPrompt with
        | Some text when not (String.IsNullOrWhiteSpace text) ->
            createObj
                [ "mode", box "primary"
                  "permission", permissionObj role
                  "prompt", box text
                  "temperature", box 1.0
                  "options", box (createObj [ "temperature", box 1.0 ]) ]
        | _ ->
            createObj
                [ "mode", box "primary"
                  "permission", permissionObj role
                  "temperature", box 1.0
                  "options", box (createObj [ "temperature", box 1.0 ]) ]

    let private hiddenAgent (role: Role) (systemPrompt: string) : obj =
        createObj
            [ "mode", box "primary"
              "hidden", box true
              "permission", permissionObj role
              "prompt", box systemPrompt
              "temperature", box 1.0
              "options", box (createObj [ "temperature", box 1.0 ]) ]

    let managerAgentConfig (prompt: string option) : obj = primaryAgent Role.Manager prompt

    let orchestratorAgentConfig (prompt: string option) : obj = primaryAgent Role.Orchestrator prompt

    let engineerAgentConfig (prompt: string option) : obj = primaryAgent Role.Engineer prompt

    /// Companion Session Y: tool set is exactly { chronicle } (ENFORCER-010).
    /// System prompt for B-record distillation with chronicle tool protocol.
    let bloggerAgentConfig (prompt: string) : obj = hiddenAgent Role.Blogger prompt

    /// InternalLeaf Bookkeeper Host stub (AGENT-002): hidden; ToolRegistry gates js-bookkeeper by attachment.
    let bookkeeperAgentConfig (prompt: string) : obj =
        let pairs =
            [ yield "*", box "deny"
              yield "external_directory", box "allow"
              for name in knownToolNames do
                  yield name, box "deny" ]

        createObj
            [ "mode", box "primary"
              "hidden", box true
              "permission", box (createObj pairs)
              "prompt", box prompt
              "temperature", box 1.0
              "options", box (createObj [ "temperature", box 1.0 ]) ]

    let devopsAgentConfig (prompt: string option) : obj = primaryAgent Role.DevOps prompt

    let planAgentConfig (prompt: string option) : obj = primaryAgent Role.Plan prompt
