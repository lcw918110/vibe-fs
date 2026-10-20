namespace Wanxiangshu.OpenCode

open System.Collections.Generic

/// HOST-012: cross-instance shared state — module-level singletons that all
/// plugin instances (root + worktree) read and write through the same
/// reference.
module SharedState =

    /// Cross-instance session parent map.
    val SessionParents: Dictionary<string, string>

    /// Cross-instance session directory map.
    val SessionDirectories: Dictionary<string, string>
