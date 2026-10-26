namespace Wanxiangshu.Repository.Programming.Js.OpenCode

open System
open System.Threading.Tasks
open Fable.Core
open Fable.Core.JsInterop
open FsToolkit.ErrorHandling
open Wanxiangshu.Host
open Wanxiangshu.Foundation.Identity
open Wanxiangshu.OpenCode
open Wanxiangshu.Participant.Provider
open Wanxiangshu.Repository.Knowledge.Casebook
open Wanxiangshu.Repository.Programming.Js

/// Bookkeeper provider verb: one JavaScript program atomically reshapes the
/// staged question/answer Case. The sandbox has no filesystem capability.
///
/// The product decisions (prose, DSL, write-once rule, rendering) live in
/// `BookkeeperPolicy`; this binding keeps the sandbox call and the host tool
/// shape.
module JsBookkeeperTool =

    [<Import("bookkeeperStaging", "wanxiangli/casebook")>]
    let private staging: obj = jsNative

    let private prose language path = BookkeeperPolicy.prose language path

    /// Resolve the staged transaction and the program source; every refusal is
    /// rendered at this boundary, so the sandbox only ever sees a proven call.
    let private stageRequest
        (language: ProviderLanguage)
        (args: HostToolArguments)
        (sessionId: SessionId)
        : Result<string * string * string * string, string> =
        result {
            let! txId =
                BookkeeperRuntime.tryTxId (SessionId.value sessionId)
                |> Result.requireSome (prose language BookkeeperPolicy.Path.NoTransaction)

            let! program =
                args.OptionalText "program"
                |> Result.requireSome (prose language BookkeeperPolicy.Path.ProgramRequired)

            let! question, answer = unbox<Result<string * string, string>> (staging?snapshot (txId))
            return txId, program, question, answer
        }

    /// Parse and commit the sandbox's returned JSON. The parse and the staged
    /// apply are two sequential Result steps, not a decision ladder.
    let private finishBookkeeper
        (language: ProviderLanguage)
        (txId: string)
        (staged: BookkeeperPolicy.StagedMutation)
        (json: string)
        : string =
        result {
            let! value =
                JsToolsData.parse json
                |> Result.mapError (fun failure -> BookkeeperPolicy.failed language (JsFailure.reason failure))

            do!
                unbox<Result<unit, string>> (staging?apply (txId, staged.Question, staged.Answer))
                |> Result.mapError (fun err -> BookkeeperPolicy.failed language err)

            return BookkeeperPolicy.succeeded language value
        }
        |> function
            | Ok result -> result
            | Error rendered -> rendered

    let private runBookkeeperProgram language sessionId name intent callId txId program question answer : Task<string> =
        task {
            let staged = BookkeeperPolicy.newStaged ()
            let api = BookkeeperPolicy.createApi language question answer staged

            let settle (receipt: obj) : Task<obj> =
                task {
                    if string receipt?state = "completed" then
                        let rendered =
                            finishBookkeeper language txId staged (JS.JSON.stringify receipt?value)

                        return emitJsExpr (receipt, rendered) "Object.assign({}, $0, {value:$1})"
                    else
                        return receipt
                }

            let options =
                createObj
                    [ "name" ==> name
                      "program" ==> program
                      "intent" ==> intent
                      "callId" ==> callId
                      "baseClass" ==> BookkeeperPolicy.runtimeBaseClass
                      "api" ==> api
                      "capabilities" ==> [||]
                      "root" ==> "."
                      "executionBudgetMs" ==> 10000
                      "outputBoundBytes" ==> (1 <<< 20)
                      "syncMembers"
                      ==> [| "bookkeeper.question"
                             "bookkeeper.answer"
                             "bookkeeper.setQuestion"
                             "bookkeeper.setAnswer" |]
                      "settle" ==> settle ]

            let session = JsPrograms.forSession sessionId
            let! result = unbox<Task<obj>> (session?execute (options))

            if string result?state = "completed" then
                return string result?value
            else
                return BookkeeperPolicy.failed language (string result?code + ": " + string result?reason)
        }

    let private executeStaged language (args: HostToolArguments) sessionId callId =
        match stageRequest language args sessionId with
        | Error err -> Task.FromResult(BookkeeperPolicy.failed language err)
        | Ok(txId, program, question, answer) ->
            runBookkeeperProgram
                language
                (SessionId.value sessionId)
                (args.Text "name")
                (args.Text "intent")
                callId
                txId
                program
                question
                answer

    let private executeCore (args: HostToolArguments) (sessionId: SessionId) callId =
        let language = BookkeeperPolicy.callLanguage ()

        match JsToolWorkflow.conflictingInputReason args.Raw with
        | Some reason -> Task.FromResult(BookkeeperPolicy.failed language reason)
        | None -> executeStaged language args sessionId callId

    let execute (args: HostToolArguments) (sessionId: SessionId) = executeCore args sessionId ""

    let spec (factory: HostToolFactory) : ToolSpec =
        let language = BookkeeperPolicy.descriptionLanguage ()

        { Name = "todowrite-for-bookkeeper"
          Description = BookkeeperPolicy.assembleDescription language
          Arguments =
            [ "name", ToolHostCodec.stringSchemaDescribed (JsToolPolicy.argNameProse ()) factory
              "program", ToolHostCodec.stringSchemaDescribed (prose language BookkeeperPolicy.Path.ArgProgram) factory
              "intent", ToolHostCodec.stringSchemaDescribed (JsToolPolicy.argIntentProse ()) factory ]
          Execute =
            fun args ctx ->
                let callId =
                    match ctx.ProviderRunId, ctx.ToolCallId with
                    | Some run, Some call -> ProviderRunIdentity.value run + ":" + ToolCallId.value call
                    | _ -> ""

                executeCore args (SessionId.create ctx.SessionId) callId }
