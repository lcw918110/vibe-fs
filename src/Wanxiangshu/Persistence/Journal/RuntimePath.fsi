namespace Wanxiangshu.Persistence.Journal

open System.Threading.Tasks

module RuntimePath =
    /// 主工作树的本机状态目录；不读取或迁入旧 Git 数据。
    val stateRoot: workspace: string -> Task<string>
    val initializeStateRoot: workspace: string -> Task<string>
    val forWorkspace: workspace: string -> Task<string>
    val forStateRoot: stateDirectory: string -> string
