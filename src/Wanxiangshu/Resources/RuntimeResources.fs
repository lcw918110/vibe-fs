namespace Wanxiangshu.Resources

open System
open Wanxiangshu.Participant.Provider

/// Bundle of package runtime data loaded once at plugin init.
type RuntimeResources =
    {
        Prompts: PromptCatalog
        /// Phase 2: bilingual provider tree roots present (`resources/provider/{en,zh-CN}`).
        ProviderLanguageRootsReady: bool
        /// Per-language catalogs. The installed `Prompts` view stays the English
        /// seed the system transform repairs; Host-config projection reads the
        /// bound language from these.
        EnglishPrompts: PromptCatalog
        SimplifiedChinesePrompts: PromptCatalog
    }

module RuntimeResources =

    // DSL-MUTABLE: resource — process-local installed runtime resources singleton
    let mutable private installed: RuntimeResources option = None

    /// Single install site: plugin constructor before any consumer runs.
    let install (resources: RuntimeResources) : unit = installed <- Some resources

    let current () : RuntimeResources =
        match installed with
        | Some resources -> resources
        | None ->
            raise (
                InvalidOperationException(
                    "RuntimeResources not installed; call RuntimeResources.install (RuntimeResources.load ()) at plugin init"
                )
            )

    /// The prompt catalog for a session-bound language, resolved from the
    /// installed bundle. Never reads the global preference: a bound session
    /// must not observe a preference change (provider-language-004).
    let promptsFor (lang: ProviderLanguage) : PromptCatalog =
        let resources = current ()

        match lang with
        | ProviderLanguage.English -> resources.EnglishPrompts
        | ProviderLanguage.SimplifiedChinese -> resources.SimplifiedChinesePrompts
