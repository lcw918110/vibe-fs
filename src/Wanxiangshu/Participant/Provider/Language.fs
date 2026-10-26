namespace Wanxiangshu.Participant.Provider

open System
open Fable.Core
open Fable.Core.JsInterop

/// PROMPT-017 / HOST-026: provider-facing world language. The closed algebra
/// stays here as the host-side type; parsing, the preference ladder and the
/// live global preference are owned by `wanxiangli/resources`.
[<RequireQualifiedAccess>]
type ProviderLanguage =
    | English
    | SimplifiedChinese

[<AutoOpen>]
module private ProviderLanguageLibrary =

    [<Import("providerLanguageStore", "wanxiangli/resources")>]
    let providerLanguageStore: obj = jsNative

[<RequireQualifiedAccess>]
module ProviderLanguage =

    type private Locale =
        { ExternalName: string
          Label: string
          ResourceDirectory: string
          ResourceFileName: string }

    let private locale =
        function
        | ProviderLanguage.English ->
            { ExternalName = "English"
              Label = "en"
              ResourceDirectory = "en"
              ResourceFileName = "en.md" }
        | ProviderLanguage.SimplifiedChinese ->
            { ExternalName = "SimplifiedChinese"
              Label = "zh-CN"
              ResourceDirectory = "zh-CN"
              ResourceFileName = "zh-CN.md" }

    let externalName language = (locale language).ExternalName

    let label language = (locale language).Label

    let resourceDirectory language = (locale language).ResourceDirectory

    /// Locale leaf filename under a semantic resource directory (§4.7.8).
    let resourceFileName language = (locale language).ResourceFileName

    let private ofExternalName (name: string) : ProviderLanguage =
        if name = "SimplifiedChinese" then
            ProviderLanguage.SimplifiedChinese
        else
            ProviderLanguage.English

    let tryParse (raw: string) : ProviderLanguage option =
        let lib = providerLanguageStore
        let found: obj = lib?tryParseLanguage (raw)

        if isNull found then
            None
        else
            Some(ofExternalName (unbox<string> found))

    let parse (raw: string) : ProviderLanguage =
        let lib = providerLanguageStore
        ofExternalName (unbox<string> (lib?parseLanguage raw))

    let private observation (value: string option) : obj =
        match value with
        | Some raw -> box raw
        | None -> null

    let private decodePreferenceResult (resolved: obj) : Result<ProviderLanguage, string> =
        if unbox<bool> resolved?ok then
            Ok(ofExternalName (unbox<string> resolved?value))
        else
            Error(unbox<string> resolved?error)

    let private resolvePreferenceObservation (raw: string) : Result<ProviderLanguage, string> =
        let lib = providerLanguageStore
        let resolved: obj = lib?resolvePreference (raw, null, null, null, null)
        decodePreferenceResult resolved

    /// The sole policy for interpreting the raw provider-language preference.
    let fromPreferenceObservation (observation: string option) : Result<ProviderLanguage, string> =
        match observation with
        | None -> Ok ProviderLanguage.English
        | Some raw when String.IsNullOrWhiteSpace raw -> Ok ProviderLanguage.English
        | Some raw -> resolvePreferenceObservation raw

    /// Resolves global preference through the normative preference ladder:
    /// 1. Explicit environment variable (WANXIANGSHU_PROVIDER_LANGUAGE)
    /// 2. Host config preference (opencode.json language)
    /// 3. IDE / System locale (VSCODE_NLS_CONFIG, POSIX LC_ALL/LANG, Node Intl)
    /// 4. Default fallback: ProviderLanguage.English
    let fromObservationLadder
        (explicit: string option)
        (hostConfig: string option)
        (vscodeNls: string option)
        (posixLocale: string option)
        (intlLocale: string option)
        : Result<ProviderLanguage, string> =
        let lib = providerLanguageStore

        let resolved: obj =
            lib?resolvePreference (
                observation explicit,
                observation hostConfig,
                observation vscodeNls,
                observation posixLocale,
                observation intlLocale
            )

        decodePreferenceResult resolved

    /// HOST-026: child / attached / InternalLeaf language = owner | commissioner.
    let inheritFrom (owner: ProviderLanguage) : ProviderLanguage = owner

[<RequireQualifiedAccess>]
module GlobalProviderLanguage =

    /// HOST-026: the live global preference is the only language authority.
    /// Every Class A render reads it, so changing the setting changes the
    /// language of the next request even mid-session.
    let refresh (resolve: unit -> ProviderLanguage) : unit =
        let lib = providerLanguageStore
        lib?refresh (fun () -> ProviderLanguage.externalName (resolve ()))
        |> ignore

    let current () : ProviderLanguage =
        let lib = providerLanguageStore
        let name: string = lib?current ()

        if name = "SimplifiedChinese" then
            ProviderLanguage.SimplifiedChinese
        else
            ProviderLanguage.English

    let setHostConfigPreference (raw: string) : unit =
        let lib = providerLanguageStore
        lib?setHostConfigPreference raw

    let clearHostConfigPreference () : unit =
        let lib = providerLanguageStore
        lib?clearHostConfigPreference ()

    let hostConfigPreference () : string option =
        let lib = providerLanguageStore
        let v: obj = lib?hostConfigPreference ()
        if isNull v then None else Some(unbox<string> v)
