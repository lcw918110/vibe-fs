namespace Wanxiangshu.OpenCode

module PluginSessionWiring =

    /// SyncDelegate runtime, attached only when a durable journal exists.
    val attach: boot: PluginBoot.Boot -> host: PluginHostWiring.Host -> unit
