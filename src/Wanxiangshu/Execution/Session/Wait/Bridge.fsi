namespace Wanxiangshu.Execution.Session.Wait

open System.Threading.Tasks

module CausalWaitBridge =
    val target: workspace: string -> IWaitDiagnosticSink
    val toPlainObject: snapshot: DiagnosticWaitSnapshot -> obj
    val writeSnapshot: workspace: string -> snapshot: DiagnosticWaitSnapshot -> Task<unit>
