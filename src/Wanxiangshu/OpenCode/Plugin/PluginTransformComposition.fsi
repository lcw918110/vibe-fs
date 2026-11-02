namespace Wanxiangshu.OpenCode

open System.Threading.Tasks

module PluginTransformComposition =
    val create: boot: PluginBoot.Boot -> host: PluginHostWiring.Host -> (obj -> obj -> Task<unit>)
