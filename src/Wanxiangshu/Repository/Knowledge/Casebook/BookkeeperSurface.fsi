namespace Wanxiangshu.Repository.Knowledge.Casebook

open System.Threading.Tasks

/// JS-native surface for the Bookkeeper runtime delegating staged case
/// transaction state to wanxiangli/casebook.
module CasebookBookkeeperSurface =

    val renderSuccess: languageName: string -> value: obj -> string

    val createRefreshPrompt: input: obj -> string

    val setRuntime: port: obj -> ownerDescriptors: obj -> obj

    val resetRuntime: unit -> unit

    val bindSession: sessionId: string -> txId: string -> ownerSessionId: string -> unit
    val unbindSession: sessionId: string -> unit
    val txIdFor: sessionId: string -> string

    val beginTransaction: txId: string -> question: string -> answer: string -> unit

    val abort: txId: string -> unit

    val snapshot: txId: string -> obj

    val take: txId: string -> obj

    val sessionId: value: string -> obj

    val sessionValue: value: obj -> string

    val acceptedSession: value: string -> obj

    val acceptedPrompt: unit -> obj

    val failedPrompt: reason: string -> obj

    val aborted: unit -> obj

    val completed: value: string -> obj
