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
/// The library owns each edit. This binding supplies product prose, runs the
/// program and commits the edit after accepting its returned value.
module JsBookkeeperTool =

    [<Import("bookkeeperStaging", "wanxiangli/casebook")>]
    let private staging: obj = jsNative

    let private prose language path = BookkeeperPolicy.prose language path

    let private describeEditFailure language key =
        let path =
            match key with
            | "set-question-once" -> BookkeeperPolicy.Path.SetQuestionOnce
            | "set-question-string" -> BookkeeperPolicy.Path.SetQuestionString
            | "set-answer-once" -> BookkeeperPolicy.Path.SetAnswerOnce
            | "set-answer-string" -> BookkeeperPolicy.Path.SetAnswerString
            | other -> invalidOp ("Unknown casebook edit failure: " + other)

        prose language path

    let private stageRequest
        (language: ProviderLanguage)
        (args: HostToolArguments)
        (sessionId: SessionId)
        : Result<string * obj, string> =
        result {
            let! txId =
                BookkeeperRuntime.tryTxId (SessionId.value sessionId)
                |> Result.requireSome (prose language BookkeeperPolicy.Path.NoTransaction)

            let! program =
                args.OptionalText "program"
                |> Result.requireSome (prose language BookkeeperPolicy.Path.ProgramRequired)

            let! edit = unbox<Result<obj, string>> (staging?edit (txId, describeEditFailure language))
            return program, edit
        }

    /// Validate the returned JSON before committing the edit.
    let private finishBookkeeper (language: ProviderLanguage) (edit: obj) (json: string) : string =
        result {
            let! value =
                JsToolsData.parse json
                |> Result.mapError (fun failure -> BookkeeperPolicy.failed language (JsFailure.reason failure))

            do!
                unbox<Result<unit, string>> (edit?commit ())
                |> Result.mapError (fun err -> BookkeeperPolicy.failed language err)

            return BookkeeperPolicy.succeeded language value
        }
        |> function
            | Ok result -> result
            | Error rendered -> rendered

    let private runBookkeeperProgram language sessionId name intent callId program (edit: obj) : Task<string> =
        task {
            let api: obj = edit?api

            let settle (receipt: obj) : Task<obj> =
                task {
                    if string receipt?state = "completed" then
                        let rendered = finishBookkeeper language edit (JS.JSON.stringify receipt?value)

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
                      "baseClass" ==> edit?runtimeBaseClass
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
        | Ok(program, edit) ->
            runBookkeeperProgram
                language
                (SessionId.value sessionId)
                (args.Text "name")
                (args.Text "intent")
                callId
                program
                edit

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
