namespace Wanxiangshu.Resources

open Wanxiangshu.Participant.Provider

type RuntimeResources =
    {
        Prompts: PromptCatalog
        ProviderLanguageRootsReady: bool
        /// Per-language prompt catalogs, resolved once at install. The installed
        /// `Prompts` view stays the English seed the system transform repairs;
        /// Host-config projection reads the bound language from here.
        EnglishPrompts: PromptCatalog
        SimplifiedChinesePrompts: PromptCatalog
    }

module RuntimeResources =
    val install: resources: RuntimeResources -> unit
    val current: unit -> RuntimeResources

    /// The prompt catalog for a session-bound language, resolved from the
    /// installed bundle. Never reads the global preference: a bound session
    /// must not observe a preference change (provider-language-004).
    val promptsFor: lang: ProviderLanguage -> PromptCatalog
