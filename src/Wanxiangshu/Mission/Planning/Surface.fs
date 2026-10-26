namespace Wanxiangshu.Mission.Planning

open System
open System.Threading.Tasks
open Fable.Core.JsInterop
open Wanxiangshu.OpenCode

module PlanningSurface =

    let private sequenceOfJs (value: obj) : int64 =
        if not (emitJsExpr value "typeof $0 === 'bigint' && $0 >= 0n") then
            invalidArg (nameof value) "Plan cursor must be a nonnegative bigint"

        unbox<int64> value

    let assembleTenureMessages (messages: obj) (tenureObj: obj) (messageCursors: obj) : obj =
        let optionalText (name: string) =
            let item = tenureObj?(name)

            if isNull item || jsTypeof item <> "string" || String.IsNullOrEmpty(unbox<string> item) then
                None
            else
                Some(unbox<string> item)

        let tenure =
            { WorkId = unbox<string> tenureObj?workId
              IncumbencyId = unbox<string> tenureObj?incumbencyId
              Stage = unbox<string> tenureObj?stage
              OpeningCursor = sequenceOfJs tenureObj?openingCursor
              IsFreshHandover = unbox<bool> tenureObj?isFreshHandover
              BarrierToolCallId = optionalText "barrierToolCallId"
              SuccessorTenureId = optionalText "successorTenureId" }

        let cursors =
            emitJsExpr messageCursors "Object.entries($0)"
            |> unbox<obj array array>
            |> Array.map (fun entry -> unbox<string> entry.[0], sequenceOfJs entry.[1])
            |> Map.ofArray

        let assembled =
            TenureIsolation.assemble
                (unbox<obj array> messages
                 |> Array.toList
                 |> List.choose (fun raw ->
                     ProviderWireCapture.decodeCapturedMessage raw
                     |> Option.map (fun message ->
                         { Id = ProviderWireDecode.hostMessageId raw
                           Role = message.Role
                           IsSynthetic = ProviderWireCapture.isSyntheticMessage raw
                           HasSemanticParts = not (List.isEmpty message.Parts)
                           Raw = raw })))
                tenure
                cursors

        box
            {| messages = assembled.Messages |> List.toArray
               reanchorRequested = assembled.ReanchorRequested |}

    let validateWorkId (workId: string) : obj =
        match PlanPath.validateWorkId workId with
        | Ok key -> box {| ok = true; key = key; error = null |}
        | Error err ->
            box
                {| ok = false
                   key = null
                   error = err |}

    let canonicalWorkKey (workId: string) : obj =
        match PlanPath.canonicalWorkKey workId with
        | Ok key -> box {| ok = true; key = key; error = null |}
        | Error err ->
            box
                {| ok = false
                   key = null
                   error = err |}

    let resolvePlanPath (root: string) (workId: string) : obj =
        match PlanPath.resolvePlanPath root workId with
        | Ok path ->
            box
                {| ok = true
                   path = path
                   error = null |}
        | Error err ->
            box
                {| ok = false
                   path = null
                   error = err |}

    let readAndValidatePlan (root: string) (workId: string) : obj =
        match PlanTools.readAndValidatePlan root workId with
        | Ok(_, digest, path) ->
            box
                {| ok = true
                   digest = digest
                   path = path
                   error = null |}
        | Error err ->
            box
                {| ok = false
                   digest = null
                   path = null
                   error = err |}


    let private extractStringOpt (v: obj) : string option =
        if isNull v then
            None
        elif emitJsExpr v "Array.isArray($0)" then
            let arr = unbox<obj array> v

            if arr.Length = 0 || isNull arr.[0] then
                None
            else
                Some(unbox<string> arr.[0])
        elif emitJsExpr v "typeof $0 === 'string'" then
            Some(unbox<string> v)
        else
            None

    let private extractInt64Opt (v: obj) : int64 option =
        if emitJsExpr v "typeof $0 === 'bigint'" then
            Some(unbox<int64> v)
        else
            None

    let ensurePlanWorkOpened (gitCommonDir: string) (root: string) (sessionId: string) (observedHead: obj) : Task<obj> =
        task {
            let head = extractInt64Opt observedHead

            match! PlanWorkOpen.ensurePlanWorkOpened gitCommonDir root sessionId head with
            | Ok() -> return box {| ok = true; error = null |}
            | Error err -> return box {| ok = false; error = err |}
        }

    let private refusedDeliver () : obj =
        box
            {| ok = false
               relayed = false
               delivered = false
               retiredIncumbencyId = null
               retirementOutcome = null
               digest = null
               path = null
               execSessionId = null
               nextIncumbencyId = null
               message = null
               error = "lifecycle deliver is retired; review is the only entry" |}

    let executeDeliver
        (gitCommonDir: string)
        (root: string)
        (workId: string)
        (incumbencyId: string)
        (providerRunId: string)
        (toolCallId: string)
        (hasBlocker: obj)
        (execution: obj)
        (retirementCursor: obj)
        : Task<obj> =
        ignore gitCommonDir
        ignore root
        ignore workId
        ignore incumbencyId
        ignore providerRunId
        ignore toolCallId
        ignore hasBlocker
        ignore execution
        ignore retirementCursor
        Task.FromResult(refusedDeliver ())

    let startPostS3Execution
        (gitCommonDir: string)
        (root: string)
        (workId: string)
        (providerRunId: string)
        (toolCallId: string)
        (hasBlocker: obj)
        (execution: obj)
        (retirementCursor: obj)
        : Task<obj> =
        ignore gitCommonDir
        ignore root
        ignore workId
        ignore providerRunId
        ignore toolCallId
        ignore hasBlocker
        ignore execution
        ignore retirementCursor
        Task.FromResult(refusedDeliver ())

    let reconcileBoundExecution
        (gitCommonDir: string)
        (workId: string)
        (execSessionId: string)
        (observedPrompt: string)
        (blockers: string array)
        : obj =
        ignore gitCommonDir
        ignore workId
        ignore execSessionId
        ignore observedPrompt
        ignore blockers
        box
            {| ok = false
               delivered = false
               error = "lifecycle deliver is retired; review is the only entry" |}

    // The delegate argument arrives as a plain JavaScript function
    // (intent) => Promise<view>; call it through JS interop so currying
    // never gets in the way.
    let private delegateOfJs (delegateVal: obj) : string -> Task<string> =
        fun (intent: string) -> unbox<Task<string>> (emitJsExpr (delegateVal, intent) "$0($1)")

    let executeAsk (gitCommonDir: string) (workId: string) (intent: string) (delegateTo: obj) : Task<obj> =
        let delegateFn = delegateOfJs delegateTo

        task {
            if String.IsNullOrWhiteSpace gitCommonDir then
                return
                    box
                        {| ok = false
                           kind = null
                           view = null
                           status = null
                           error = "git common directory is missing" |}
            else
                let! outcome = PlanTools.executeAsk gitCommonDir workId intent delegateFn

                return
                    match outcome with
                    | Error error ->
                        box
                            {| ok = false
                               kind = null
                               view = null
                               status = null
                               error = error |}
                    | Ok asked ->
                        box
                            {| ok = true
                               kind = asked.Kind
                               view = asked.View
                               status = asked.Status
                               error = null |}
        }


    let private parseFindings (findingsVal: obj) : Result<(string * string option) list, string> =
        if isNull findingsVal then
            Error "findings must be an array of {key, value} pairs"
        elif emitJsExpr findingsVal "Array.isArray($0)" then
            let arr = unbox<obj array> findingsVal

            let items =
                arr
                |> Array.toList
                |> List.map (fun (item: obj) ->
                    let key: obj = item?key
                    let value: obj = item?value

                    if
                        isNull key
                        || not (emitJsExpr key "typeof $0 === 'string'")
                        || String.IsNullOrWhiteSpace(unbox<string> key)
                    then
                        None
                    else
                        let valueOpt =
                            if isNull value then
                                None
                            elif emitJsExpr value "typeof $0 === 'string'" then
                                Some(unbox<string> value)
                            else
                                None

                        Some(unbox<string> key, valueOpt))

            let valid = items |> List.choose id

            if valid.Length <> items.Length then
                Error "each finding requires a non-empty key; value may be a string or null (to delete)"
            else
                Ok valid
        else
            Error "findings must be an array of {key, value} pairs"

    let executeReview
        (gitCommonDir: string)
        (workId: string)
        (incumbencyId: string)
        (requestId: string)
        (findings: obj)
        (note: obj)
        : Task<obj> =
        // note is accepted for writing and never returned.
        task {
            match parseFindings findings with
            | Error err ->
                return
                    box
                        {| ok = false
                           outcome = null
                           gaps = null
                           error = err |}
            | Ok _gaps ->
                if String.IsNullOrWhiteSpace gitCommonDir then
                    return
                        box
                            {| ok = false
                               outcome = null
                               gaps = null
                               error = "git common directory is missing" |}
                else
                    let reply =
                        Wanxiangshu.Mission.Lifecycle.DurableWorkHost.submitReview
                            gitCommonDir
                            workId
                            requestId
                            "Plan"
                            incumbencyId
                            requestId
                            findings
                            null
                            ""
                            null

                    let status = string reply?status
                    let accepted = status = "Committed" || status = "Replayed" || status = "Observed"

                    return
                        box
                            {| ok = accepted
                               kind = reply?kind
                               milestone = reply?milestone
                               error = if accepted then null else reply?reason |}
        }

    let isReviewFinished (gitCommonDir: string) (workId: string) (incumbencyId: string) : bool =
        match Wanxiangshu.Mission.Lifecycle.DurableWorkHost.inspected gitCommonDir workId with
        | Some work when work.ActiveId = Some incumbencyId -> work.Reviewed
        | _ -> false

    let PlanningSurface: obj =
        box
            {| assembleTenureMessages = assembleTenureMessages
               validateWorkId = validateWorkId
               canonicalWorkKey = canonicalWorkKey
               resolvePlanPath = resolvePlanPath
               readAndValidatePlan = readAndValidatePlan
               ensurePlanWorkOpened = ensurePlanWorkOpened
               executeDeliver = executeDeliver
               startPostS3Execution = startPostS3Execution
               reconcileBoundExecution = reconcileBoundExecution
               executeAsk = executeAsk
               executeReview = executeReview
               isReviewFinished = isReviewFinished |}
