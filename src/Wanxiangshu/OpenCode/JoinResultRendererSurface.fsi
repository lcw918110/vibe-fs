namespace Wanxiangshu.OpenCode

/// Entry-local join rendering owner surface. It accepts a plain delivered view
/// and keeps join item unions/private non-empty batches inside the renderer.
module JoinResultRendererSurface =

    val renderAgentCompletion: language: string -> agentName: string -> view: string -> string
