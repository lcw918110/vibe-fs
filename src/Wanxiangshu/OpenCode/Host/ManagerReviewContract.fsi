namespace Wanxiangshu.OpenCode.Host

module ManagerReviewContract =

    /// Decorates the tool definition for the four manager review tools by adding
    /// the contract property to parameters and appending contract to required.
    /// Non-review tools are returned unmodified.
    val decorateDefinition: toolInput: obj -> toolOutput: obj -> unit

    /// Hides the contract argument by saving its original property descriptor (or undefined)
    /// under a private module Symbol on the args object with enumerable:false, configurable:true,
    /// and deleting the contract property. Fails atomically if not extensible/frozen/non-configurable.
    val hide: args: obj -> unit

    val hideForCall: owner: ProtocolArgumentCall option -> args: obj -> unit

    val classifyHiddenArguments: owner: ProtocolArgumentCall option -> args: obj -> HiddenProtocolArguments

    /// Restores the contract argument from the private module Symbol on the args object.
    /// Idempotent (no-op if not present). Throws TypeError if object is frozen/non-extensible.
    val restore: args: obj -> unit

    /// Normal after callbacks release only their own stash; an unidentified
    /// callback may restore an unidentified stash, never an identified one.
    val restoreForCall: owner: ProtocolArgumentCall option -> args: obj -> unit
