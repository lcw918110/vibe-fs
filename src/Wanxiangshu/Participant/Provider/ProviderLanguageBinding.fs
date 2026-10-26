namespace Wanxiangshu.Participant.Provider

open System
open Fable.Core
open Wanxiangshu.Foundation.Identity
open Wanxiangshu.Participant.Provider

/// HOST-026: the global preference is the only language authority.
///
/// No session binding and no durable language record exist. Every Class A
/// render resolves through `GlobalProviderLanguage`, which this module keeps
/// in sync with the observed preference ladder; changing the setting changes
/// the language of the next request even mid-session.
[<RequireQualifiedAccess>]
module ProviderLanguageBinding =

    let setHostConfigPreference (raw: string) : unit =
        GlobalProviderLanguage.setHostConfigPreference raw

    let clearHostConfigPreferenceForTests () : unit =
        GlobalProviderLanguage.clearHostConfigPreference ()

    let private unwrapOrRaise (result: Result<ProviderLanguage, string>) : ProviderLanguage =
        match result with
        | Ok value -> value
        | Error error -> raise (InvalidOperationException error)

    [<Emit("typeof Intl !== 'undefined' && Intl.DateTimeFormat ? (Intl.DateTimeFormat().resolvedOptions().locale || '') : ''")>]
    let private jsIntlLocale () : string = jsNative

    let private readIntlLocale () : string option =
        try
            let loc = jsIntlLocale ()
            if String.IsNullOrEmpty loc then None else Some loc
        with _ ->
            None

    let readGlobalPreference () : ProviderLanguage =
        let explicit =
            Environment.GetEnvironmentVariable "WANXIANGSHU_PROVIDER_LANGUAGE"
            |> Option.ofObj

        let hostConfig = GlobalProviderLanguage.hostConfigPreference ()

        let vscodeNls =
            Environment.GetEnvironmentVariable "VSCODE_NLS_CONFIG" |> Option.ofObj

        let posixLocale =
            Environment.GetEnvironmentVariable "LC_ALL"
            |> Option.ofObj
            |> Option.orElseWith (fun () -> Environment.GetEnvironmentVariable "LC_MESSAGES" |> Option.ofObj)
            |> Option.orElseWith (fun () -> Environment.GetEnvironmentVariable "LANG" |> Option.ofObj)

        let intlLocale = readIntlLocale ()

        ProviderLanguage.fromObservationLadder explicit hostConfig vscodeNls posixLocale intlLocale
        |> unwrapOrRaise

    /// Publish the currently observed preference into the global language
    /// holder. Called at startup, on host config change and in tests that
    /// change the preference.
    let refreshGlobalLanguage () : unit =
        GlobalProviderLanguage.refresh readGlobalPreference

    let forSessionText (sessionText: string) : ProviderLanguage =
        ignore sessionText
        GlobalProviderLanguage.current ()
