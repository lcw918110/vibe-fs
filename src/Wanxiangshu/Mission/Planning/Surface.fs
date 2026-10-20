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
        let tenure =
            { WorkId = unbox<string> tenureObj?workId
              IncumbencyId = unbox<string> tenureObj?incumbencyId
              Stage = unbox<string> tenureObj?stage
              OpeningCursor = sequenceOfJs tenureObj?openingCursor
              IsFreshHandover = unbox<bool> tenureObj?isFreshHandover }

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

    let readPlan (root: string) (workId: string) : obj =
        match PlanPath.readPlan root workId with
        | Ok content ->
            box
                {| ok = true
                   content = content
                   error = null |}
        | Error err ->
            box
                {| ok = false
                   content = null
                   error = err |}

    let rewritePlanAtomic (root: string) (workId: string) (content: string) : obj =
        match PlanPath.rewritePlanAtomic root workId content with
        | Ok(path, digest) ->
            box
                {| ok = true
                   path = path
                   digest = digest
                   error = null |}
        | Error err ->
            box
                {| ok = false
                   path = null
                   digest = null
                   error = err |}

    let editPlanAtomic (root: string) (workId: string) (patches: obj) : obj =
        let patchList =
            if isNull patches then
                []
            else
                let arr = unbox<obj array> patches

                arr
                |> Array.toList
                |> List.map (fun p -> (unbox<string> p?find, unbox<string> p?put))

        match PlanPath.editPlanAtomic root workId patchList with
        | Ok(path, digest) ->
            box
                {| ok = true
                   path = path
                   digest = digest
                   error = null |}
        | Error err ->
            box
                {| ok = false
                   path = null
                   digest = null
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

    let private createExecutionOfJs (execution: obj) : string -> string -> Task<Result<string, string>> =
        fun (operationId: string) (agent: string) ->
            task {
                try
                    let! sessionId =
                        unbox<Task<string>> (emitJsExpr (execution, operationId, agent) "$0.create($1, $2)")

                    if String.IsNullOrWhiteSpace sessionId then
                        return Error "Execution session creation returned no session identity"
                    else
                        return Ok sessionId
                with ex ->
                    return Error(sprintf "Execution session creation outcome is unknown: %s" ex.Message)
            }

    let private promptExecutionOfJs (execution: obj) : string -> string -> string -> Task<DeliverySpawnOutcome> =
        fun (execSessionId: string) (agent: string) (prompt: string) ->
            task {
                try
                    let! raw =
                        unbox<Task<obj>> (emitJsExpr (execution, execSessionId, agent, prompt) "$0.prompt($1, $2, $3)")

                    let kind = if isNull raw then "" else string raw?kind

                    match kind with
                    | "Started" -> return DeliverySpawnOutcome.Started execSessionId
                    | "NotStarted" -> return DeliverySpawnOutcome.NotStarted(string raw?reason)
                    | "Unknown" -> return DeliverySpawnOutcome.Unknown(string raw?reason)
                    | _ -> return DeliverySpawnOutcome.Unknown "Execution prompt returned an unknown outcome"
                with ex ->
                    return DeliverySpawnOutcome.Unknown ex.Message
            }

    let private discardExecutionOfJs (execution: obj) : string -> Task<Result<unit, string>> =
        fun (execSessionId: string) ->
            task {
                try
                    do! unbox<Task<unit>> (emitJsExpr (execution, execSessionId) "$0.discard($1)")

                    return Ok()
                with ex ->
                    return Error ex.Message
            }

    let ensurePlanWorkOpened (gitCommonDir: string) (root: string) (sessionId: string) (observedHead: obj) : Task<obj> =
        task {
            let head = extractInt64Opt observedHead

            match! PlanWorkOpen.ensurePlanWorkOpened gitCommonDir root sessionId head with
            | Ok() -> return box {| ok = true; error = null |}
            | Error err -> return box {| ok = false; error = err |}
        }

    let private retirementBlockersOfJs (observation: obj) () =
        let value = emitJsExpr observation "typeof $0 === 'function' ? $0() : $0"

        if not (emitJsExpr value "typeof $0 === 'boolean'") then
            invalidOp "Expected a boolean retirement resource observation"

        if unbox<bool> value then
            [| "retirement-blocked" |]
        else
            [||]

    let private deliverReceiptOfJs (outcome: Result<DeliverResult, string>) : obj =
        match outcome with
        | Ok res ->
            box
                {| ok = true
                   relayed = res.Relayed
                   delivered = res.Delivered
                   retiredIncumbencyId = res.RetiredIncumbencyId
                   retirementOutcome = res.RetirementOutcome
                   digest = res.Digest
                   path = res.Path
                   execSessionId = res.ExecSessionId
                   nextIncumbencyId = res.NextIncumbencyId
                   message = res.Message
                   error = null |}
        | Error err ->
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
                   error = err |}

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
        let createSession = createExecutionOfJs execution
        let promptSession = promptExecutionOfJs execution
        let discardSession = discardExecutionOfJs execution

        task {
            let! outcome =
                match extractInt64Opt retirementCursor with
                | None -> Task.FromResult(Error "Plan retirement cursor evidence is required")
                | Some cursor ->
                    if String.IsNullOrWhiteSpace gitCommonDir then
                        Task.FromResult(Error "git common directory is missing")
                    else
                        PlanTools.executeDeliver
                            gitCommonDir
                            root
                            workId
                            incumbencyId
                            providerRunId
                            toolCallId
                            (retirementBlockersOfJs hasBlocker)
                            createSession
                            promptSession
                            discardSession
                            cursor

            return deliverReceiptOfJs outcome
        }

    /// The post-S3 delivery entry of an S1-S3 work. A call whose fence already
    /// owns a recorded retirement replays that recorded outcome with zero
    /// writes instead of opening another tenure.
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
        let createSession = createExecutionOfJs execution
        let promptSession = promptExecutionOfJs execution
        let discardSession = discardExecutionOfJs execution

        task {
            let! outcome =
                match extractInt64Opt retirementCursor with
                | None -> Task.FromResult(Error "Plan retirement cursor evidence is required")
                | Some cursor ->
                    if String.IsNullOrWhiteSpace gitCommonDir then
                        Task.FromResult(Error "git common directory is missing")
                    else
                        PlanTools.startPostS3Execution
                            gitCommonDir
                            root
                            workId
                            providerRunId
                            toolCallId
                            (retirementBlockersOfJs hasBlocker)
                            createSession
                            promptSession
                            discardSession
                            cursor

            return deliverReceiptOfJs outcome
        }

    let reconcileBoundExecution gitCommonDir workId execSessionId observedPrompt blockers : obj =
        match PlanTools.reconcileBoundExecution gitCommonDir workId execSessionId observedPrompt blockers with
        | Ok result ->
            box
                {| ok = true
                   delivered = result.Delivered
                   error = null |}
        | Error reason ->
            box
                {| ok = false
                   delivered = false
                   error = reason |}

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

    let executeJsPlan (root: string) (workId: string) (action: string) (content: obj) (patches: obj) : obj =
        let contentOpt = extractStringOpt content

        let patchList =
            if isNull patches then
                None
            else
                let arr = unbox<obj array> patches

                Some(
                    arr
                    |> Array.toList
                    |> List.map (fun p -> (unbox<string> p?find, unbox<string> p?put))
                )

        match PlanTools.executeJsPlan root workId action contentOpt patchList with
        | Ok res ->
            box
                {| ok = true
                   output = res
                   error = null |}
        | Error err ->
            box
                {| ok = false
                   output = null
                   error = err |}

    let reviewTargetFor (stage: string) : string = ReviewPlanBackend.reviewTargetFor stage

    let private parseFindings (findingsVal: obj) : Result<(string * string) list, string> =
        if isNull findingsVal then
            Error "findings must be an array of {acceptance_criteria, work_plan} pairs"
        elif emitJsExpr findingsVal "Array.isArray($0)" then
            let arr = unbox<obj array> findingsVal

            let items =
                arr
                |> Array.toList
                |> List.map (fun (item: obj) ->
                    let acceptance: obj = item?acceptance_criteria
                    let workPlan: obj = item?work_plan

                    if
                        isNull acceptance
                        || isNull workPlan
                        || not (emitJsExpr acceptance "typeof $0 === 'string'")
                        || not (emitJsExpr workPlan "typeof $0 === 'string'")
                        || String.IsNullOrEmpty(unbox<string> acceptance)
                        || String.IsNullOrWhiteSpace(unbox<string> workPlan)
                    then
                        None
                    else
                        Some(unbox<string> acceptance, unbox<string> workPlan))

            let valid = items |> List.choose id

            if valid.Length <> items.Length then
                Error "each finding requires non-empty acceptance_criteria and work_plan strings"
            else
                Ok valid
        else
            Error "findings must be an array of {acceptance_criteria, work_plan} pairs"

    let private gapsToJs (gaps: (string * string) list) : obj array =
        gaps
        |> List.map (fun (acceptance, workPlan) ->
            box
                {| acceptance_criteria = acceptance
                   work_plan = workPlan |})
        |> Array.ofList

    let private renderReviewOutcome (outcome: PlanReviewOutcome) : string =
        match outcome with
        | PlanReviewOutcome.Promoted -> "Promoted"
        | PlanReviewOutcome.Stayed -> "Stayed"

    let executeReview
        (gitCommonDir: string)
        (workId: string)
        (incumbencyId: string)
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
            | Ok gaps ->
                if String.IsNullOrWhiteSpace gitCommonDir then
                    return
                        box
                            {| ok = false
                               outcome = null
                               gaps = null
                               error = "git common directory is missing" |}
                else
                    match! ReviewPlanBackend.executeReview gitCommonDir workId incumbencyId gaps with
                    | Error err ->
                        return
                            box
                                {| ok = false
                                   outcome = null
                                   gaps = null
                                   error = PlanReviewError.render err |}
                    | Ok review ->
                        return
                            box
                                {| ok = true
                                   outcome = renderReviewOutcome review.Outcome
                                   target = review.Target
                                   gaps = gapsToJs review.Gaps
                                   error = Unchecked.defaultof<string> |}
        }

    let PlanningSurface: obj =
        box
            {| assembleTenureMessages = assembleTenureMessages
               validateWorkId = validateWorkId
               canonicalWorkKey = canonicalWorkKey
               resolvePlanPath = resolvePlanPath
               readPlan = readPlan
               readAndValidatePlan = readAndValidatePlan
               rewritePlanAtomic = rewritePlanAtomic
               editPlanAtomic = editPlanAtomic
               ensurePlanWorkOpened = ensurePlanWorkOpened
               executeDeliver = executeDeliver
               startPostS3Execution = startPostS3Execution
               reconcileBoundExecution = reconcileBoundExecution
               executeAsk = executeAsk
               executeJsPlan = executeJsPlan
               executeReview = executeReview
               reviewTargetFor = reviewTargetFor |}
