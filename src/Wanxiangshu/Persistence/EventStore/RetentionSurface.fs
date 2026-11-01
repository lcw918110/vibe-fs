namespace Wanxiangshu.Persistence.EventStore

open System.Threading.Tasks
open Fable.Core.JsInterop

/// 本机文件保留观察。不再提供 Git snapshot 导出、导入或 remote 同步入口。
[<RequireQualifiedAccess>]
module RetentionSurface =

    let retainedWriterIdsAt (stateRoot: string) (nowMs: float) : string[] =
        match ProcessEventLog.readStreamsAt stateRoot nowMs with
        | Ok streams -> streams |> List.map fst |> List.toArray
        | Error error -> failwith (sprintf "%A" error)

    /// 测试边界只调用已有的本机段整理，不创建 Git snapshot，也不读取远端。
    let reorganizeWithSegmentCap (stateRoot: string) (capBytes: int) : Task<obj> =
        task {
            if capBytes <= 0 then
                invalidArg "capBytes" "segment cap must be positive"

            let! outcome =
                ProcessEventLog.withStoreLock stateRoot (fun () ->
                    ProcessEventLog.withSegmentCap capBytes (fun () ->
                        ProcessEventLog.writerPhysicalMetadata stateRoot
                        |> List.fold
                            (fun result writer ->
                                result
                                |> Result.bind (fun () ->
                                    ProcessEventLog.convergeWriterFiles stateRoot writer.WriterId))
                            (Ok()))
                    |> Task.FromResult)

            return
                match outcome with
                | Ok() -> createObj [ "ok" ==> true ]
                | Error error -> createObj [ "ok" ==> false; "error" ==> error ]
        }
