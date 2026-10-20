namespace Wanxiangshu.Participant.Provider

open System.Threading.Tasks
open Fable.Core.JsInterop
open Wanxiangshu.Foundation.Identity
open Wanxiangshu.Foundation

/// Provider-language owner boundary. Language values, session binding and
/// localized resources cross as strings and plain objects; ProviderLanguage,
/// SessionProviderLanguage and host/runtime state stay private.
module ProviderLanguageSurface =

    let private languageOf (raw: string) : ProviderLanguage = ProviderLanguage.parse raw

    let private languageName language = ProviderLanguage.externalName language

    let private resultOf result =
        match result with
        | Ok value ->
            box
                {| ok = true
                   value = languageName value
                   error = "" |}
        | Error error ->
            box
                {| ok = false
                   value = ""
                   error = error |}

    let private mapOf (value: obj) : Map<string, string> =
        if isNull value then
            Map.empty
        else
            let keys: string array = emitJsExpr value "Object.keys($0)"

            keys
            |> Array.toList
            |> List.map (fun key -> key, string (emitJsExpr (value, key) "$0[$1]"))
            |> Map.ofList

    let parse (raw: string) : string =
        ProviderLanguage.parse raw |> languageName

    let nameOf (raw: string) : string = languageOf raw |> languageName

    let tryParse (raw: string) : obj =
        match ProviderLanguage.tryParse raw with
        | Some language -> box (languageName language)
        | None -> null

    let label (raw: string) : string = ProviderLanguage.label (languageOf raw)

    let resourceDirectory (raw: string) : string =
        ProviderLanguage.resourceDirectory (languageOf raw)

    let resourceFileName (raw: string) : string =
        ProviderLanguage.resourceFileName (languageOf raw)

    let inheritFrom (raw: string) : string =
        ProviderLanguage.inheritFrom (languageOf raw) |> languageName

    let clearAllForTests () : unit =
        Wanxiangshu.OpenCode.ProviderLanguageBinding.clearHostConfigPreferenceForTests ()
        Wanxiangshu.OpenCode.ProviderLanguageBinding.refreshGlobalLanguage ()

    let setHostConfigPreference (raw: string) : unit =
        Wanxiangshu.OpenCode.ProviderLanguageBinding.setHostConfigPreference raw

    let readGlobalPreference () : string =
        Wanxiangshu.OpenCode.ProviderLanguageBinding.readGlobalPreference ()
        |> languageName

    let refreshGlobalLanguage () : unit =
        Wanxiangshu.OpenCode.ProviderLanguageBinding.refreshGlobalLanguage ()

    let languageOfSession (sessionId: string) : string =
        ProviderProse.languageOf (SessionId.create sessionId) |> languageName

    let languageRootsPresent () : bool =
        ProviderResources.languageRootsPresent ()

    let relativePath (language: string) (semanticPath: string) : string =
        ProviderResources.relativePath (languageOf language) semanticPath

    let exists (language: string) (semanticPath: string) : bool =
        ProviderResources.exists (languageOf language) semanticPath

    let readText (language: string) (semanticPath: string) : string =
        ProviderResources.readText (languageOf language) semanticPath

    let requireLanguagePair (semanticPath: string) : unit =
        ProviderResources.requireLanguagePair semanticPath

    let substitute (template: string) (substitutions: obj) : string =
        ProviderProse.substitute template (mapOf substitutions)

    let loadBookkeeperSystem (language: string) : string =
        Wanxiangshu.Resources.PromptResources.loadBookkeeperSystemFor (languageOf language)

    /// Exercise the real host transform at the provider-language boundary for
    /// the Bookkeeper-owned system segment. The attachment fixture is private;
    /// host-owned system bytes remain caller data and are never rewritten.
    let transformBookkeeperSystem (sessionId: string) (system: string array) : Task<obj> =
        task {
            let input = createObj [ "sessionID" ==> sessionId; "model" ==> createObj [] ]
            let output = createObj [ "system" ==> system ]

            let! _ =
                Wanxiangshu.OpenCode.ProviderSystemTransform.createWith
                    (fun _ -> None)
                    (fun _ -> Task.FromResult None)
                    input
                    output

            return box {| system = unbox<string array> output?system |}
        }

    /// Same boundary for a public role. The label crosses as a string; the
    /// transform itself still decides the role from durable authority first and
    /// only falls back to the supplied label when no profile exists.
    let transformRoleSystem (sessionId: string) (roleLabel: string) (system: string array) : Task<obj> =
        task {

            let role = Roles.tryParseRole roleLabel

            let input = createObj [ "sessionID" ==> sessionId; "model" ==> createObj [] ]
            let output = createObj [ "system" ==> system ]

            let! _ =
                Wanxiangshu.OpenCode.ProviderSystemTransform.createWith
                    (fun _ -> role)
                    (fun _ -> Task.FromResult None)
                    input
                    output

            return box {| system = unbox<string array> output?system |}
        }
