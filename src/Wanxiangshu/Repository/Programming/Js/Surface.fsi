namespace Wanxiangshu.Repository.Programming.Js

open Wanxiangshu.Foundation

type JsSurface =
    { ToolName: string
      RoleName: string
      Capabilities: Set<JsCapability>
      Members: JsCapabilityFragment list
      Description: string
      BaseClassSource: string
      Examples: string list }

module JsToolGenerator =
    val membersFor: capabilities: Set<JsCapability> -> JsCapabilityFragment list
    val toolNameFor: roleName: string -> string
    val renderBaseClass: prose: JsCanonicalDescription.Prose -> capabilities: Set<JsCapability> -> string

    val renderDescription:
        prose: JsCanonicalDescription.Prose -> roleName: string -> capabilities: Set<JsCapability> -> string

    val renderExamples:
        prose: JsCanonicalDescription.Prose -> roleName: string -> capabilities: Set<JsCapability> -> string list

    val generate:
        roleName: string -> capabilities: Set<ToolPermission> -> prose: JsCanonicalDescription.Prose -> JsSurface option

    val isGeneratedToolName: roleName: string -> capabilities: Set<ToolPermission> -> toolName: string -> bool
    val memberBinding: roleName: string -> capabilities: Set<ToolPermission> -> memberName: string -> string option
