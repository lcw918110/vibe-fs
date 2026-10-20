namespace Wanxiangshu.Resources

open Wanxiangshu.Foundation
open Wanxiangshu.Participant.Provider

module RuntimeResourceAssembly =

    let loadFor (lang: ProviderLanguage) : RuntimeResources =
        let prompts = PromptResources.loadForLanguage lang

        { Prompts = prompts
          ProviderLanguageRootsReady = ProviderResources.languageRootsPresent ()
          EnglishPrompts = PromptResources.loadForLanguage ProviderLanguage.English
          SimplifiedChinesePrompts = PromptResources.loadForLanguage ProviderLanguage.SimplifiedChinese }

    let load () : RuntimeResources = loadFor ProviderLanguage.English
