namespace Wanxiangshu.OpenCode

open Fable.Core
open Fable.Core.JsInterop
open Wanxiangshu.Host.Ports

[<AllowNullLiteral>]
type private ILiveHostConfigStore =
    abstract accept: nextConfig: obj -> unit
    abstract read: path: string array -> obj
    abstract apply: settings: obj array -> obj

module private LiveHostConfigInterop =
    [<Import("LiveHostConfigStore", "wanxiangli/capability")>]
    let storeClass: obj = jsNative

    [<Emit("new $0($1)")>]
    let createStore (cls: obj) (initialConfig: obj) : ILiveHostConfigStore = jsNative

/// The live host config, shared between the adapter (which owns the host object)
/// and the product (which reads and writes settings through `IHostConfigPort`).
///
/// The host delivers its config object through the `config` hook, long after the
/// ports were built. The holder delegates live state to `wanxiangli/capability`'s
/// `LiveHostConfigStore`, keeping the same `IHostConfigPort` instance and seeing
/// the live object, never a stale snapshot.
type LiveHostConfig(config: obj) =

    let store = LiveHostConfigInterop.createStore LiveHostConfigInterop.storeClass config

    let safeString (rawVal: obj) : string =
        if isNull rawVal then "" else string rawVal

    let parseSettingValue (kind: string) (rawVal: obj) : HostSettingValue =
        if kind = "bool" then
            HostSettingValue.Bool(unbox<bool> rawVal)
        else
            HostSettingValue.Text(safeString rawVal)

    let parseGapPath (res: obj) : string list =
        if isNull res?gapPath then []
        else Array.toList (unbox<string array> res?gapPath)

    let mapReadResult (res: obj) : Result<HostSettingValue option, HostError> =
        if not (unbox<bool> res?ok) then
            Error(HostError.Refused(unbox<string> res?error))
        elif not (unbox<bool> res?found) then
            Ok None
        else
            let kind = unbox<string> res?kind
            let v = res?value
            Ok(Some(parseSettingValue kind v))

    let settingToObj (s: RequiredSetting) : obj =
        match s.Value with
        | HostSettingValue.Bool b ->
            createObj
                [ "path" ==> List.toArray s.Path
                  "kind" ==> "bool"
                  "value" ==> box b ]
        | HostSettingValue.Text t ->
            createObj
                [ "path" ==> List.toArray s.Path
                  "kind" ==> "text"
                  "value" ==> box t ]

    let mapApplyResult (res: obj) : Result<SettingGap option, HostError> =
        if not (unbox<bool> res?ok) then
            Error(HostError.Refused(unbox<string> res?error))
        elif unbox<bool> res?hasGap then
            let path = parseGapPath res
            let reason = unbox<string> res?gapReason
            Ok(Some { Path = path; Reason = reason })
        else
            Ok None

    /// Adopt the config object the host just delivered. The port instance stays
    /// the same, so the product keeps reading the live config.
    member _.Accept(nextConfig: obj) : unit =
        store.accept(nextConfig)

    interface IHostConfigPort with
        member _.Read(path: string list) : Result<HostSettingValue option, HostError> =
            let res = store.read(List.toArray path)
            mapReadResult res

        member _.Apply(settings: RequiredSetting list) : Result<SettingGap option, HostError> =
            let settingsArray = settings |> List.map settingToObj |> List.toArray
            let res = store.apply(settingsArray)
            mapApplyResult res
