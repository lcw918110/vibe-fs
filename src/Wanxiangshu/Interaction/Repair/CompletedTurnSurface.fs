namespace Wanxiangshu.Interaction.Repair

open Fable.Core.JsInterop
open Wanxiangshu.Composition.Turn
open Wanxiangshu.Foundation
open Wanxiangshu.Foundation.Identity
open Wanxiangshu.OpenCode

/// JS-native semantic surface for completed-turn classification.
/// Host message parts and turn outcomes remain typed behind this boundary.
module CompletedTurnSurface =

    let private text (value: obj) =
        if isNull value then "" else string value

    let private optionalText (value: obj) : string option =
        if isNull value then None else Some(text value)

    let private arrayOf (value: obj) : obj array =
        if isNull value then [||] else unbox<obj array> value

    let private partsOf (value: obj) : MessagePart array =
        arrayOf value |> Array.choose HostMessageCodec.decodePart

    let private optionalModel (value: obj) : OpencodeModel option =
        if isNull value then
            None
        else
            Some
                { providerID = text value?providerID
                  modelID = text value?modelID
                  variant = optionalText value?variant }

    let private optionalRole (value: string) : Role option =
        if System.String.IsNullOrWhiteSpace value then
            None
        else
            Roles.tryParseRole value

    let private outcomeName (value: obj) : string * string option =
        match value with
        | :? ReconcileProgram.SnapshotObservation -> "TurnUnknown", None
        | :? ReconcileProgram.TurnOutcome as outcome ->
            match outcome with
            | ReconcileProgram.TurnInProgress -> "TurnInProgress", None
            | ReconcileProgram.TurnNeedsContinuation reason -> "TurnNeedsContinuation", Some reason
            | ReconcileProgram.TurnCompleted -> "TurnCompleted", None
            | ReconcileProgram.TurnAborted reason -> "TurnAborted", Some reason
            | ReconcileProgram.TurnFailed error -> "TurnFailed", Some error
        | _ -> "TurnUnknown", None

    let private classifiedToJs (value: obj) : obj =
        let name, reason = outcomeName value

        box
            {| kind = name
               reason = reason |> Option.defaultValue null |}

    let partsText (parts: obj) : string =
        CompletedTurnClassifier.partsText (partsOf parts)

    let partsSessionText (parts: obj) : string =
        CompletedTurnClassifier.partsSessionText (partsOf parts)

    let hasToolCallPart (parts: obj) : bool =
        CompletedTurnClassifier.hasToolCallPart (partsOf parts)

    let isAbortErrorName (name: string) : bool =
        CompletedTurnClassifier.isAbortErrorName (optionalText name)

    /// provider-attempt-recovery-008: formal content that cannot be a terminal (empty / XML-only).
    let formalContentUnusable (parts: obj) : bool =
        CompletedTurnClassifier.formalContentUnusable (partsOf parts)

    let classifyOutcome (completed: bool) (finish: string) (errorName: string) (parts: obj) : obj =
        CompletedTurnClassifier.classifyOutcome
            completed
            (optionalText (box finish))
            (optionalText (box errorName))
            (partsOf parts)
        |> classifiedToJs

    let needsInteractionRepair (role: string) (completed: bool) (finish: string) (parts: obj) : bool =
        let roleValue = optionalRole role
        let finishValue = optionalText (box finish)
        let typedParts = partsOf parts

        let classified =
            CompletedTurnClassifier.classifyOutcome completed finishValue None typedParts

        CompletedTurnClassifier.needsInteractionRepair roleValue classified typedParts

    let private repairDecisionName =
        function
        | CompletedTurnClassifier.RepairDefectDecision.RequestRepair -> "RequestRepair"
        | CompletedTurnClassifier.RepairDefectDecision.AwaitRepairTerminal -> "AwaitRepairTerminal"
        | CompletedTurnClassifier.RepairDefectDecision.NoRepair -> "NoRepair"

    let repairDefectDecision (currentAttemptIsRepair: bool) (completed: bool) (finish: string) (parts: obj) : string =
        let classified =
            CompletedTurnClassifier.classifyOutcome completed (optionalText (box finish)) None (partsOf parts)

        match classified with
        | :? ReconcileProgram.SnapshotObservation as observation ->
            CompletedTurnClassifier.decideRepairDefect
                currentAttemptIsRepair
                (Some observation)
                (ReconcileProgram.TurnNeedsContinuation "private-snapshot-observation")
        | :? ReconcileProgram.TurnOutcome as outcome ->
            CompletedTurnClassifier.decideRepairDefect currentAttemptIsRepair None outcome
        | _ -> CompletedTurnClassifier.RepairDefectDecision.NoRepair
        |> repairDecisionName

    /// interaction-authority-023: retry-continuation suppression holds only while
    /// the attempt is unsettled — either settlement (stable observation terminal
    /// or durable execution terminal) releases it.
    let repairSuppressionHolds
        (isRetryContinuation: bool)
        (hasDurableTerminal: bool)
        (completed: bool)
        (finish: string)
        (parts: obj)
        : bool =
        let classified =
            CompletedTurnClassifier.classifyOutcome completed (optionalText (box finish)) None (partsOf parts)

        match classified with
        | :? ReconcileProgram.SnapshotObservation as observation ->
            CompletedTurnClassifier.retryContinuationSuppressesRepair
                isRetryContinuation
                (Some observation)
                (ReconcileProgram.TurnNeedsContinuation "private-snapshot-observation")
                hasDurableTerminal
        | :? ReconcileProgram.TurnOutcome as outcome ->
            CompletedTurnClassifier.retryContinuationSuppressesRepair
                isRetryContinuation
                None
                outcome
                hasDurableTerminal
        | _ -> false

    let roleOfAgent (agent: string) (fallback: string) : string =
        let result =
            CompletedTurnClassifier.roleOfAgent (optionalText (box agent)) (optionalRole fallback)

        result |> Option.map Roles.roleLabel |> Option.defaultValue ""

    let private messageOf (value: obj) : SessionMessage =
        { Id = text value?id
          Role =
            if System.String.IsNullOrWhiteSpace(text value?role) then
                "assistant"
            else
                text value?role
          Agent = optionalText value?agent
          Finish = optionalText value?finish
          ErrorName = optionalText value?errorName
          Model = optionalModel value?model
          ParentId = optionalText value?parentId
          CreatedAt = None
          Completed =
            if isNull value?completed then
                false
            else
                unbox<bool> value?completed
          IsCompaction = false
          PromptKey = None
          Parts = partsOf value?parts
          PartIds = [||]
          ToolParts = [||] }

    let private partToJs (part: MessagePart) : obj =
        match part with
        | MessagePart.Text value -> box {| kind = "text"; text = value |}
        | MessagePart.Reasoning value -> box {| kind = "reasoning"; text = value |}
        | MessagePart.ToolCall(callId, name, args) ->
            box
                {| kind = "tool-call"
                   callId = callId
                   name = name
                   args = args |}
        | MessagePart.ToolResult(callId, result) ->
            box
                {| kind = "tool-result"
                   callId = callId
                   result = result |}
        | MessagePart.Activity kind -> box {| kind = "activity"; activity = kind |}

    let buildTurn
        (session: string)
        (physical: string)
        (authorityRoot: string)
        (message: obj)
        (roleFallback: string)
        (directory: string)
        : obj =
        let typed =
            CompletedTurnClassifier.buildTurn
                (SessionId.create session)
                (PhysicalUserMessageId.create physical)
                (AuthorityRootUserMessageId.create authorityRoot)
                (messageOf message)
                (optionalRole roleFallback)
                (optionalText (box directory))

        let outcome, reason = outcomeName (box typed.Outcome)

        box
            {| session = SessionId.value typed.SessionId
               providerRun = ProviderRunIdentity.value typed.ProviderRun
               role = typed.Role |> Option.map Roles.roleLabel |> Option.defaultValue ""
               directory = typed.Directory |> Option.defaultValue null
               finish = typed.Finish |> Option.defaultValue null
               errorName = typed.ErrorName |> Option.defaultValue null
               model =
                typed.Model
                |> Option.map (fun model ->
                    box
                        {| providerID = model.providerID
                           modelID = model.modelID
                           variant = model.variant |> Option.defaultValue null |})
                |> Option.defaultValue null
               parts = typed.Parts |> Array.map partToJs
               outcome = outcome
               reason = reason |> Option.defaultValue null
               hasObservation = typed.Observation.IsSome |}
