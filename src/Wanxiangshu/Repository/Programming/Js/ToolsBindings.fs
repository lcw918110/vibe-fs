namespace Wanxiangshu.Repository.Programming.Js

open System.Threading.Tasks
open System.Text
open Fable.Core
open Fable.Core.JsInterop
open FsToolkit.ErrorHandling
open Wanxiangshu.Foundation.Identity

type JsDelegationHost =
    { Delegate: string -> obj -> Task<obj>
      Fission: obj -> Task<obj>
      Horizon: unit -> Task<obj>
      Review: obj -> obj -> Task<obj> }

/// JS-010/JS-016: the api object injected into the sandbox — the only
/// authority a model program sees. Every member returns a JSON-compatible
/// object; failures carry `{ ok: false, code, reason }` with stable codes
/// (JS-019). Mutations write as-is: each edit/write is atomically persisted
/// through MutationFs at call time; the staging buffer records what was
/// actually written, so a later failure keeps earlier writes on disk (JS-012).
module JsToolsBindings =

    [<Import("FileSession", "wanxiangli/runtime")>]
    let private fileSession: obj -> obj = jsNative

    [<Import("RuntimeTools", "wanxiangli/runtime")>]
    let private runtimeTools: string -> obj = jsNative

    let internal failureObj (failure: JsFailure) : obj =
        createObj
            [ "ok" ==> false
              "code" ==> JsFailure.code failure
              "reason" ==> JsFailure.reason failure ]

    let createLedger (persistence: IJsTransactionPersistence option) : JsAsIsLedger =
        { Persistence = persistence
          Appends = ResizeArray() }

    /// Observe a fired append so a rejected task never becomes an unhandled
    /// rejection on a path that does not settle the ledger (a parked site
    /// suspends before settlement).
    [<Emit("$0.catch(() => undefined)")>]
    let private observeRejection (append: obj) : obj = jsNative

    /// H1: fire the Prepared append before the write lands. Returns the
    /// transaction id so the matching Committed can be fired after the write;
    /// a failed write leaves that Prepared as interrupt evidence ([015]).
    let private firePrepared
        (ledger: JsAsIsLedger)
        (root: string)
        (mutation: JsStagedMutation)
        : JsTransactionId option =
        match ledger.Persistence with
        | None -> None
        | Some durable ->
            let prepared =
                { TransactionId = JsTransactionId.generate ()
                  WorkspaceRoot = root
                  Mutation = JsTransactionFacts.ofMutation mutation }

            let pending = durable.AppendPrepared prepared
            observeRejection (box pending) |> ignore
            ledger.Appends.Add pending
            Some prepared.TransactionId

    /// H1: fire the Committed append after the write landed.
    let private fireCommitted (ledger: JsAsIsLedger) (transactionId: JsTransactionId option) : unit =
        match ledger.Persistence, transactionId with
        | Some durable, Some transactionId ->
            let pending = durable.AppendCommitted transactionId
            observeRejection (box pending) |> ignore
            ledger.Appends.Add pending
        | _ -> ()


    let private mutationOf (value: obj) =
        match string value?kind with
        | "rewrite" -> JsStagedMutation.Rewrite(string value?path, string value?originalText, string value?newText)
        | "create" -> JsStagedMutation.Create(string value?path, string value?text)
        | "remove" -> JsStagedMutation.Remove(string value?path)
        | "move" -> JsStagedMutation.Move(string value?path, string value?destination)
        | _ -> invalidArg "mutation" "unknown physical file effect"

    let private files root (staging: ResizeArray<JsStagedMutation>) (reads: ResizeArray<JsExplicitFileRead>) ledger =
        fileSession (
            createObj
                [ "root" ==> root
                  "before"
                  ==> fun (value: obj) -> box (firePrepared ledger root (mutationOf value))
                  "after"
                  ==> fun (value: obj) (token: obj) ->
                      let mutation = mutationOf value
                      staging.Add mutation
                      fireCommitted ledger (unbox<JsTransactionId option> token)
                  "read"
                  ==> fun (path: string) (text: string) -> reads.Add { Path = path; ResultBytes = text } ]
        )

    [<Emit("Promise.reject(Object.assign(new Error($1), { code: $0, reason: $1 }))")>]
    let private rejectedJsFailure (code: string) (reason: string) : obj = jsNative

    let private invokeAsk (invoke: string -> Task<Result<unit, string>>) text : Task<obj> =
        task {
            match! invoke text with
            | Ok() -> return createObj [ "__programPark" ==> createObj [ "kind" ==> "asked"; "question" ==> text ] ]
            | Error reason ->
                let failure = JsFailure.AskRefused reason
                return! unbox<Task<obj>> (rejectedJsFailure (JsFailure.code failure) (JsFailure.reason failure))
        }

    let makeAskMember (ask: (string -> Task<Result<unit, string>>) option) =
        "ask"
        ==> fun (question: obj) (_stack: obj) ->
            let text =
                if emitJsExpr question "typeof $0 === 'string'" then
                    unbox<string> question
                else
                    ""

            match ask with
            | Some invoke when not (System.String.IsNullOrWhiteSpace text) -> box (invokeAsk invoke text)
            | _ ->
                let failure =
                    JsFailure.AskRefused "ask requires a question and an available user channel"

                rejectedJsFailure (JsFailure.code failure) (JsFailure.reason failure)

    let makeReviewMember (host: JsDelegationHost option) : (string * obj) =
        "review"
        ==> System.Func<obj, obj, obj>(fun (findings: obj) (note: obj) ->
            match host with
            | None -> box (task { return failureObj JsFailure.DelegationUnavailable })
            | Some delegation -> box (delegation.Review findings note))

    let private makeDelegateMember (host: JsDelegationHost option) (name: string) =
        name
        ==> fun (payload: obj) ->
            match host with
            | None -> box (task { return failureObj JsFailure.DelegationUnavailable })
            | Some delegation -> box (delegation.Delegate name payload)

    let private makeFissionMember (host: JsDelegationHost option) =
        "fission"
        ==> fun (payload: obj) ->
            match host with
            | Some delegation -> box (delegation.Fission payload)
            | None -> box (task { return failureObj JsFailure.DelegationUnavailable })

    /// `horizon()` returns the horizon roster as a data envelope (who is
    /// still away, each participant's status/workState/record and the four
    /// calibration numbers). Without an installed delegation host the member
    /// refuses with DELEGATION_UNAVAILABLE instead of pretending.
    let makeHorizonMember (host: JsDelegationHost option) : (string * obj) =
        "horizon"
        ==> fun () ->
            match host with
            | Some delegation -> box (delegation.Horizon())
            | None -> box (task { return failureObj JsFailure.DelegationUnavailable })

    let createApi
        (capabilities: Set<JsCapability>)
        (root: string)
        (staging: ResizeArray<JsStagedMutation>)
        (explicitReads: ResizeArray<JsExplicitFileRead>)
        (ledger: JsAsIsLedger)
        (delegationHost: JsDelegationHost option)
        (askInvoke: (string -> Task<Result<unit, string>>) option)
        : obj =
        let file = files root staging explicitReads ledger
        let physical = runtimeTools root
        let directory = JsCapability.memberNamesFor capabilities ""
        let present memberName = Array.contains memberName directory

        createObj
            [ if present "cat" then
                  "cat" ==> file?cat
              if present "stat" then
                  "stat" ==> file?stat
              if present "whereAll" then
                  "whereAll" ==> physical?whereAll
              if present "where" then
                  "where" ==> physical?where
              if present "ls" then
                  "ls" ==> file?ls
              if present "rg" then
                  "rg" ==> physical?rg
              if present "write" then
                  "write" ==> file?write
              if present "mkdir" then
                  "mkdir" ==> file?mkdir
              if present "rm" then
                  "rm" ==> file?rm
              if present "mv" then
                  "mv" ==> file?mv
              if present "commission" then
                  makeDelegateMember delegationHost "commission"
              if present "fork" then
                  makeDelegateMember delegationHost "fork"
              if present "resume" then
                  makeDelegateMember delegationHost "resume"
              if present "devops" then
                  makeDelegateMember delegationHost "devops"
              if present "fission" then
                  makeFissionMember delegationHost
              if present "horizon" then
                  makeHorizonMember delegationHost
              if present "ask" then
                  makeAskMember askInvoke
              if present "review" then
                  makeReviewMember delegationHost ]

module JsReviewOnly =
    let private planGuardUnary (check: unit -> bool) (invoke: obj) : obj =
        emitJsExpr
            (check, invoke, JsToolsBindings.failureObj JsFailure.DraftWriteLocked)
            "((check, invoke, locked) => (a0) => check() ? invoke(a0) : locked)($0, $1, $2)"

    let private planGuardBinary (check: unit -> bool) (invoke: obj) : obj =
        emitJsExpr
            (check, invoke, JsToolsBindings.failureObj JsFailure.DraftWriteLocked)
            "((check, invoke, locked) => (a0, a1) => check() ? invoke(a0, a1) : locked)($0, $1, $2)"

    let private makeGuardedRead (check: unit -> Task<bool>) (invoke: obj) : obj =
        box (fun (argument: obj) ->
            task {
                let! allowed = check ()

                if not allowed then
                    return JsToolsBindings.failureObj JsFailure.ReviewOnly
                else
                    return! unbox<Task<obj>> (emitJsExpr (invoke, argument) "Promise.resolve($0($1))")
            })

    let scopeManagerReviewOnly (check: unit -> Task<bool>) (api: obj) : obj =
        let guarded name =
            name ==> makeGuardedRead check api?(name)

        let review = [ "cat"; "stat"; "ls"; "rg" ] |> List.map guarded |> createObj

        for name in [ "cat"; "stat"; "ls"; "rg" ] do
            emitJsExpr (api, name) "delete $0[$1]" |> ignore

        api?reviewOnly <- review
        api

    let scopePlanWriteOnly (check: unit -> bool) (api: obj) : obj =
        if emitJsExpr api "'write' in $0" then
            api?write <- planGuardBinary check api?write

        if emitJsExpr api "'rm' in $0" then
            api?rm <- planGuardUnary check api?rm

        if emitJsExpr api "'mv' in $0" then
            api?mv <- planGuardBinary check api?mv

        api

    let scopeReviewOnly (check: unit -> Task<bool>) (api: obj) : obj = scopeManagerReviewOnly check api
