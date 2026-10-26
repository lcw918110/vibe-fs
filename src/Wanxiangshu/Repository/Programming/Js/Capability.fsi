namespace Wanxiangshu.Repository.Programming.Js

open Wanxiangshu.Foundation

[<RequireQualifiedAccess>]
type JsCapability =
    | Read
    | Write

    | Glob
    | Grep
    | Move
    | Remove
    | Exec
    | Fork
    | Resume
    | Commission
    | DevOps
    | Fission
    | Horizon
    | Ask
    | Review
    /// A lifecycle capability, not a ToolPermission projection: `deliver`
    /// ends a program by handing over its view.
    | Park

module JsCapability =
    val ofToolPermission: permission: ToolPermission -> JsCapability option
    val ofToolCapabilities: capabilities: Set<ToolPermission> -> Set<JsCapability>

    val admitsProgrammingSurface: capabilities: Set<ToolPermission> -> bool

    val toolNameFor: roleName: string -> string

    val labelOf: capability: JsCapability -> string

    val ofLabel: label: string -> JsCapability option

    val memberNamesFor: capabilities: Set<JsCapability> -> roleName: string -> string array

    val projectRole:
        roleName: string -> capabilities: Set<ToolPermission> -> (string * Set<JsCapability> * string array) option

type JsCapabilityFragment =
    { Capability: JsCapability
      MemberName: string }

module JsFragmentRegistry =
    val write: JsCapabilityFragment
    val rm: JsCapabilityFragment
    val mv: JsCapabilityFragment
    val cat: JsCapabilityFragment
    val ls: JsCapabilityFragment
    val rg: JsCapabilityFragment
    val stat: JsCapabilityFragment
    val mkdir: JsCapabilityFragment
    val whereAll: JsCapabilityFragment
    val where: JsCapabilityFragment
    val call: JsCapabilityFragment
    val signal: JsCapabilityFragment
    val send: JsCapabilityFragment
    val fork: JsCapabilityFragment
    val resume: JsCapabilityFragment
    val commission: JsCapabilityFragment
    val devops: JsCapabilityFragment
    val fission: JsCapabilityFragment
    val horizon: JsCapabilityFragment
    val ask: JsCapabilityFragment
    val review: JsCapabilityFragment
    val park: JsCapabilityFragment
    val all: JsCapabilityFragment list
