namespace Wanxiangshu.Repository.Programming.Js

open Wanxiangshu.Foundation

type JsSurface =
    { ToolName: string
      RoleName: string
      Capabilities: Set<JsCapability>
      Members: JsCapabilityFragment list
      Description: string
      BaseClassSource: string
      Examples: string list
      RuntimeBindings: Map<string, string> }

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

type JsTransactionContext =
    new: unit -> JsTransactionContext
    member RecordGrepScan: paths: string seq -> unit
    member recordGrepScan: paths: string array -> unit
    member RecordExplicitRead: path: string -> unit
    member recordExplicitRead: path: string -> unit
    member StageWrite: path: string * content: string -> unit
    member stageWrite: path: string * content: string -> unit
    member Abort: unit -> unit
    member abort: unit -> unit
    member Commit: unit -> unit
    member commit: unit -> unit
    member GetReadSnapshots: unit -> string array
    member getReadSnapshots: unit -> string array
    member GetSubstantiveAccess: unit -> string array
    member getSubstantiveAccess: unit -> string array

module JsSurfaceExports =
    val createTransactionContext: unit -> JsTransactionContext
