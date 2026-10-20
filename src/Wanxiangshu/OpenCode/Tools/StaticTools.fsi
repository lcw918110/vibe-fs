namespace Wanxiangshu.OpenCode

open Wanxiangshu.Foundation


module StaticTools =
    val toolNames: p: ToolPermission -> string list
    val toolName: p: ToolPermission -> string
    val jsToolName: role: Role -> string
    val cognitiveUtilityRoleAllowed: role: Role -> bool
    val hostExternalNamespaceAllowedForRole: role: Role -> bool
    val knownToolNames: string list
    val permissionOfToolName: role: Role -> name: string -> ToolPermission option
    val admitsToolForRole: role: Role -> name: string -> bool
    val requestToolMap: role: Role -> allowed: Set<ToolPermission> -> Map<string, bool>
    val permissionObj: role: Role -> obj
    val managerAgentConfig: prompt: string option -> obj
    val orchestratorAgentConfig: prompt: string option -> obj
    val engineerAgentConfig: prompt: string option -> obj
    val bookkeeperAgentConfig: prompt: string -> obj
    val devopsAgentConfig: prompt: string option -> obj
    val planAgentConfig: prompt: string option -> obj
    val advisorAgentConfig: prompt: string -> obj
