namespace Wanxiangshu.OpenCode

open Wanxiangshu.Foundation


module StaticTools =
    val toolNames: p: ToolPermission -> string list
    val toolName: p: ToolPermission -> string
    val jsToolName: role: Role -> string
    val cognitiveUtilityRoleAllowed: role: Role -> bool
    val knownToolNames: string list
    val permissionOfToolName: name: string -> ToolPermission option
    val admitsToolForRole: role: Role -> name: string -> bool
    val requestToolMap: allowed: Set<ToolPermission> -> Map<string, bool>
    val permissionObj: role: Role -> obj
    val managerAgentConfig: prompt: string option -> obj
    val orchestratorAgentConfig: prompt: string option -> obj
    val engineerAgentConfig: prompt: string option -> obj
    val bloggerAgentConfig: prompt: string -> obj
    val bookkeeperAgentConfig: prompt: string -> obj
    val devopsAgentConfig: prompt: string option -> obj
    val planAgentConfig: prompt: string option -> obj
