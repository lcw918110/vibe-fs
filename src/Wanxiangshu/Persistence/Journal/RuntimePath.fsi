namespace Wanxiangshu.Persistence.Journal

open System.Threading.Tasks

module RuntimePath =
    /// 主工作树的本机状态目录；仅明确非 Git 时使用当前目录，其余定位错误透传。
    val stateRoot: workspace: string -> Task<string>
    val initializeStateRoot: workspace: string -> Task<string>
    val forWorkspace: workspace: string -> Task<string>
    /// 同步采用同一严格定位规则，不等待异步缓存。
    val stateRootSync: workspace: string -> string
    val forStateRoot: stateRoot: string -> string
