namespace Wanxiangshu.Ablation

open System
open Fable.Core
open Fable.Core.JsInterop

[<RequireQualifiedAccess>]
module AblationSettings =

    [<Import("ablationConfig", "wanxiangli/ablation")>]
    let private config: obj = jsNative

    let private readEnv (name: string) : string =
        Environment.GetEnvironmentVariable name

    let private toModePair (nodeId: string, modeWire: string) : AblationNodeId * AblationMode =
        let mode = AblationMode.parse modeWire |> Option.defaultValue AblationMode.Active
        AblationNodeId.create nodeId, mode

    let private explicitOverrides (nodeIds: AblationNodeId list) : Result<Map<AblationNodeId, AblationMode>, AblationLoadError> =
        let rawIds = nodeIds |> List.map AblationNodeId.value |> List.toArray
        let outcome: obj = config?resolveOverrides (rawIds, readEnv)

        if unbox<bool> outcome?ok then
            let pairs: (string * string)[] = outcome?pairs
            pairs |> Array.map toModePair |> Map.ofArray |> Ok
        else
            Error(InvalidMode(unbox<string> outcome?node, unbox<string> outcome?raw))

    let load () : Result<AblationRegistry, AblationLoadError> =
        let rawProfile: string = config?resolveProfile (readEnv)
        let profileOpt = Option.ofObj rawProfile

        AblationManifest.loadNodes ()
        |> Result.bind (fun document ->
            explicitOverrides (AblationManifest.nodeIds document)
            |> Result.bind (fun overrides ->
                AblationManifest.buildRegistry document profileOpt overrides))
