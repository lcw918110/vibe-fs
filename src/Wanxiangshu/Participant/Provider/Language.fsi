namespace Wanxiangshu.Participant.Provider

[<RequireQualifiedAccess>]
type ProviderLanguage =
    | English
    | SimplifiedChinese

[<RequireQualifiedAccess>]
module ProviderLanguage =
    val externalName: language: ProviderLanguage -> string
    val label: language: ProviderLanguage -> string
    val resourceDirectory: language: ProviderLanguage -> string
    val resourceFileName: language: ProviderLanguage -> string
    val tryParse: raw: string -> ProviderLanguage option
    val parse: raw: string -> ProviderLanguage
    val fromPreferenceObservation: observation: string option -> Result<ProviderLanguage, string>

    val fromObservationLadder:
        explicit: string option ->
        hostConfig: string option ->
        vscodeNls: string option ->
        posixLocale: string option ->
        intlLocale: string option ->
            Result<ProviderLanguage, string>

    val inheritFrom: owner: ProviderLanguage -> ProviderLanguage

[<RequireQualifiedAccess>]
module GlobalProviderLanguage =
    /// Re-resolve the live global preference (env → host config → locale →
    /// English). Called at startup and whenever the host preference changes.
    val refresh: resolve: (unit -> ProviderLanguage) -> unit

    /// The live global preference. The single language authority: every Class A
    /// render reads this, so a setting change takes effect on the next request.
    val current: unit -> ProviderLanguage

    val setHostConfigPreference: raw: string -> unit
    val clearHostConfigPreference: unit -> unit
    val hostConfigPreference: unit -> string option
