namespace Wanxiangshu.Ablation

type AblationMode =
    | Ablated
    | Borrowed
    | Active

type AblationNodeId = AblationNodeId of string

type AblationAudit =
    { Profile: string option
      ManifestVersion: string
      ManifestFingerprint: string
      NodeCount: int }

type ManifestNode =
    { Id: string
      Package: string
      Station: int
      Kind: string
      Parent: string option
      BorrowedSurface: string list }

type ManifestEdge =
    { From: string
      To: string
      Kind: string }

type ManifestDocument =
    { Version: string
      Nodes: ManifestNode list
      Edges: ManifestEdge list }

type AblationRegistry =
    { Modes: Map<AblationNodeId, AblationMode>
      Audit: AblationAudit
      Nodes: Map<AblationNodeId, ManifestNode> }

type AblationLoadError =
    | MissingManifest of path: string
    | InvalidManifest of reason: string
    | UnknownProfile of name: string
    | InvalidMode of node: string * raw: string
    | DagViolation of reason: string

type ProfilesDocument =
    { Profiles: Map<string, Map<string, string>> }

type ToolMapDocument = { Tools: Map<string, string> }

type FactMapDocument = { Facts: Map<string, string> }

[<RequireQualifiedAccess>]
module AblationNodeId =
    let create (raw: string) = AblationNodeId raw
    let value (AblationNodeId raw) = raw

[<RequireQualifiedAccess>]
module AblationMode =
    let parse (raw: string) =
        match raw.Trim().ToLowerInvariant() with
        | "ablated" -> Some AblationMode.Ablated
        | "borrowed" -> Some AblationMode.Borrowed
        | "active" -> Some AblationMode.Active
        | _ -> None

    let wire mode =
        match mode with
        | AblationMode.Ablated -> "ablated"
        | AblationMode.Borrowed -> "borrowed"
        | AblationMode.Active -> "active"

    let allowsToolSchema mode =
        match mode with
        | AblationMode.Active
        | AblationMode.Borrowed -> true
        | AblationMode.Ablated -> false

[<RequireQualifiedAccess>]
module AblationRegistry =
    let modeFor (id: AblationNodeId) (registry: AblationRegistry) =
        registry.Modes |> Map.tryFind id |> Option.defaultValue AblationMode.Ablated

    let isActive id registry =
        modeFor id registry = AblationMode.Active
