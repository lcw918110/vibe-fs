namespace Wanxiangshu.Ablation

open Fable.Core
open Fable.Core.JsInterop

/// Ablation decisions, made against a registry the caller supplies.
[<RequireQualifiedAccess>]
module AblationGate =

    [<Import("ablationGateState", "wanxiangli/ablation")>]
    let private state: obj = jsNative

    let useRegistry (registry: AblationRegistry) : unit =
        state?useRegistry (box registry) |> ignore

    let private fallbackRegistry () : obj =
        AblationSettings.load ()
        |> Result.defaultWith (fun _ -> AblationResolution.denied ())
        |> box

    let registry () : AblationRegistry =
        state?resolveRegistry (fallbackRegistry) |> unbox<AblationRegistry>

    let resetRegistry () : unit = state?resetRegistry () |> ignore

    let installedRegistry () : AblationRegistry option =
        let current: obj = state?installedRegistry ()
        current |> Option.ofObj |> Option.map unbox<AblationRegistry>

    let private modeWire (registry: AblationRegistry) (nodeId: string) : string =
        AblationRegistry.modeFor (AblationNodeId.create nodeId) registry |> AblationMode.wire

    let private isActiveNode (registry: AblationRegistry) (nodeId: string) : bool =
        AblationRegistry.isActive (AblationNodeId.create nodeId) registry

    let private tryToolNodeWire (toolName: string) : string =
        AblationToolMap.tryNode toolName |> Option.map AblationNodeId.value |> Option.toObj

    let private tryFactNodeWire (factTag: string) : string =
        AblationFactMap.tryNode factTag |> Option.map AblationNodeId.value |> Option.toObj

    let toolDenied (registry: AblationRegistry) (toolName: string) : bool =
        state?toolDenied (modeWire registry, tryToolNodeWire, toolName)

    let toolSchemaDenied (registry: AblationRegistry) (toolName: string) : bool =
        state?toolSchemaDenied (modeWire registry, tryToolNodeWire, toolName)

    let filterToolPermissionMap (registry: AblationRegistry) (permissions: Map<string, bool>) =
        permissions
        |> Map.map (fun name allowed -> allowed && not (toolSchemaDenied registry name))

    let deniedFactPath: string = state?deniedFactPath

    let factDenied (registry: AblationRegistry) (factTag: string) : bool =
        state?factDenied (modeWire registry, tryFactNodeWire, factTag)

    let primaryAgentAllowed (registry: AblationRegistry) (agentName: string) : bool =
        state?primaryAgentAllowed (isActiveNode registry, agentName)

    let fissionVisible (registry: AblationRegistry) : bool =
        state?fissionVisible (isActiveNode registry)

    let modeFor (registry: AblationRegistry) (nodeId: string) : AblationMode =
        AblationRegistry.modeFor (AblationNodeId.create nodeId) registry
