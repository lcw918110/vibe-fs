namespace Wanxiangshu.Repository.Knowledge.Casebook

open System
open System.Threading.Tasks
open Wanxiangshu.Foundation
open Wanxiangshu.Foundation.Identity
open Wanxiangshu.Foundation.Outcome
open Wanxiangshu.Interaction.Authority
open Wanxiangshu.Participant.Persona
open Wanxiangshu.Participant.Provider

/// Physical Bookkeeper request kind.
[<RequireQualifiedAccess>]
type BookkeeperRequest =
    | CaseRefresh
    | CaseFinalize

type ICasebookSessionPort =
    abstract AbortSession: childId: SessionId -> Task<Result<unit, string>>
    abstract SubscribeTerminal: childId: SessionId * (SessionId -> Result<unit, string> -> unit) -> IDisposable
    abstract SendPrompt: childId: SessionId * promptText: string * agent: string -> Task<Result<unit, string>>

    abstract CreateSiblingSession:
        ownerSessionId: SessionId * title: string * agent: string -> Task<Result<SessionId, string>>

/// Product prompts and host session effects for a library Bookkeeper transaction.
module BookkeeperRuntime =

    val createRefreshPrompt: q: string -> a: string -> relatedPaths: string list -> diff: string -> string

    val createRefreshPromptFor:
        lang: ProviderLanguage -> q: string -> a: string -> relatedPaths: string list -> diff: string -> string

    val setPort:
        port: ICasebookSessionPort ->
        resolveActiveOwner: (SessionId -> PromptAuthority.AuthorityExecutionProfile option) ->
            unit

    val resetRuntime: unit -> unit

    val bindSession: sessionId: string -> txId: string -> ownerSessionId: string -> unit

    val unbindSession: sessionId: string -> unit

    val tryConsumePromptAuthorization:
        sessionId: SessionId -> explicitAgent: string option -> text: string option -> bool

    val completePhysical: sessionId: SessionId -> outcome: Result<unit, string> -> unit

    val tryTxId: sessionId: string -> string option

    val txIdFor: sessionId: string -> string

    val isAttached: sessionId: string -> bool

    val runTransaction:
        kind: BookkeeperRequest ->
        ownerSessionId: SessionId ->
        q: string ->
        a: string ->
        observations: Observation list ->
        extraTranscript: string option ->
            Task<Result<string * string, string>>
