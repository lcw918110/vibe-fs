namespace Wanxiangshu.OpenCode

open System
open Fable.Core.JsInterop
open Wanxiangshu.Ablation
open Wanxiangshu.Foundation


module StaticTools =

    /// capability-enforcement-012: permission → provider verb names. The single
    /// map lives in wanxiangli/capability (via OfficeCapability); an empty list
    /// means the duty lives on the programming surface only (this.devops).
    let toolNames (p: ToolPermission) : string list = OfficeCapability.toolNames p

    /// capability-enforcement-012: the sole reverse lookup for tool name →
    /// permission, answered by wanxiangli/capability. The schema projection, the
    /// dispatch tool map and the execute gate all read this. A verb shared by two
    /// permissions resolves to the one this role owns.
    let permissionOfToolName (role: Role) (name: string) : ToolPermission option =
        OfficeCapability.permissionOfToolName role name

    /// The office projection of one name: true only when the role's permission
    /// set owns the permission this exact name maps to.
    let admitsToolForRole (role: Role) (name: string) : bool =
        OfficeCapability.admitsToolForRole role name


    /// JS-001: the generated todowrite-for-ROLE tool name for a role.
    let jsToolName (role: Role) : string =
        "todowrite-for-" + (string role).ToLowerInvariant()

    /// capability-enforcement-006/007: a Host-declared external (MCP) tool
    /// namespace is not an office capability, so it never enters
    /// `Roles.permissions` (capability-enforcement-006) and no per-source
    /// permission table is kept for it (capability-enforcement-007). It follows
    /// the Host utility surface (`skill`) instead: every projected office role
    /// keeps it.
    let hostExternalNamespaceAllowedForRole (role: Role) : bool = true

    /// JS-001: todowrite-for-* is allowed in the permission matrix exactly when
    /// the role's office permissions project a programming surface. Same
    /// predicate as ToolAdmissions.programmingFor and JsToolGenerator (no second list).
    let private admitsProgrammingSurface (role: Role) : bool =
        Wanxiangshu.Repository.Programming.Js.JsCapability.admitsProgrammingSurface (OfficeCapability.permissions role)

    /// Single source: OfficeCapability.permissions → OpenCode agent permission object.
    /// Emits explicit allow/deny for the full known tool name set so host schema
    /// filters and contract tests see concrete denies (not only "*").
    /// The catalog holds exactly the host provider tool names. `handoff` is
    /// fully retired. `devops` left the catalog with its ToolSpec: the host
    /// devops tool is fully retired, and the verb survives only as a
    /// programming-surface member (this.devops) projected by `Capability.fs`.
    /// `fork` / `resume` / `commission` / `review` stay in the catalog because
    /// their ToolSpec constructors are retained — they are no longer registered
    /// as host provider tools (the delegation and review lines run on the
    /// programming surface), yet every ToolSpec owner name must appear here
    /// (tool-referential-integrity). Their permission entries are therefore
    /// harmless: they match no registered tool.
    let knownToolNames =
        [ "fork"
          "resume"
          "commission"
          "join"
          "horizon"
          "fission"
          "skill"
          "assume"
          "bash-honeypot"
          "review"
          "fetch"
          "todowrite-for-engineer"
          "todowrite-for-manager"
          "todowrite-for-orchestrator"
          "todowrite-for-devops"
          "todowrite-for-bookkeeper"
          "todowrite-for-plan"
          "negotiate"
          "askPreviousScope" ]

    /// PROMPT-012: an explicit complete allow/deny map for PromptInput.tools.
    /// The office half comes from the one reverse lookup; utility tools read the
    /// one utility admission in wanxiangli/capability, the same answer the
    /// permission object and the tool admission give.
    let requestToolMap (role: Role) (allowed: Set<ToolPermission>) : Map<string, bool> =
        let registry = AblationGate.registry ()

        knownToolNames
        |> List.map (fun name ->
            name,
            (name = "negotiate"
             || (name = "askPreviousScope" && Set.contains ToolPermission.ResetScope allowed)
             || OfficeCapability.admitsUtility role name
             || (permissionOfToolName role name
                 |> Option.exists (fun permission -> Set.contains permission allowed))))
        |> Map.ofList
        |> AblationGate.filterToolPermissionMap registry

    let private defaultPermission (allowed: Set<ToolPermission>) (role: Role) name =
        let admitted =
            permissionOfToolName role name
            |> Option.exists (fun permission -> Set.contains permission allowed)

        if admitted then "allow" else "deny"

    let private jsPermission role name =
        if name = jsToolName role && admitsProgrammingSurface role then
            "allow"
        else
            "deny"

    /// 工具类的宿主准入：办公能力矩阵直接回答该角色是否被准许使用这个
    /// 工具。答案只有「准许 / 不准许」两种，所以它是一个命名判定而不是
    /// 一段嵌在别处的分支。
    let private utilityPermission (role: Role) (name: string) =
        match OfficeCapability.admitsUtility role name with
        | true -> "allow"
        | false -> "deny"

    let private permissionFor (registry: AblationRegistry) allowed role name =
        match not (AblationGate.toolSchemaDenied registry name), name, role with
        | false, _, _ -> "deny"
        // Host-registration exceptions the office matrix cannot express: the
        // Orchestrator's Fork duty runs on the programming surface, and its
        // host verb is `commission` (no office permission of its own).
        | true, "fork", Role.Orchestrator -> "deny"
        | true, "negotiate", _ -> "allow"
        | true, "askPreviousScope", role when OfficeCapability.isAllowed role ToolPermission.ResetScope -> "allow"
        | true, "askPreviousScope", _ -> "deny"
        | true, "commission", Role.Orchestrator -> "allow"
        | true, name, role when List.contains name OfficeCapability.utilityToolNames -> utilityPermission role name
        | true, "todowrite-for-bookkeeper", _ -> "deny"
        | true, name, _ when name.StartsWith "todowrite-for-" -> jsPermission role name
        | true, _, _ -> defaultPermission allowed role name

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

    let managerAgentConfig (prompt: string option) : obj = primaryAgent Role.Manager prompt

    let orchestratorAgentConfig (prompt: string option) : obj = primaryAgent Role.Orchestrator prompt

    let engineerAgentConfig (prompt: string option) : obj = primaryAgent Role.Engineer prompt



    /// InternalLeaf Bookkeeper Host stub (AGENT-002): hidden; ToolRegistry gates todowrite-for-bookkeeper by attachment.
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
