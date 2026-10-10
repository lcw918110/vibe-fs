namespace Wanxiangshu.Mission.Planning

open System
open System.Threading.Tasks
open Fable.Core.JsInterop
open Thoth.Json
open Wanxiangshu.Context.Trace
open Wanxiangshu.Foundation
open Wanxiangshu.Foundation.Identity
open Wanxiangshu.Persistence.EventStore

module PlanningSurface =

    let empty () : PlanWorkState = PlanFold.initialWorkState

    let workOpened (workId: string) (root: string) : PlanEvent = PlanEvents.workOpened workId root

    let devOpsBound (workId: string) (devopsId: string) (targetVal: obj) : PlanEvent =
        let target =
            match targetVal with
            | :? string as s -> Some s
            | :? (string list) as l -> List.tryHead l
            | :? (string array) as arr -> Array.tryHead arr
            | _ -> None

        PlanEvents.devOpsBound workId devopsId target

    let incumbencyOpened (workId: string) (incumbencyId: string) (stageVal: obj) (openingCursor: int64) : PlanEvent =
        let stage =
            match stageVal with
            | :? PlanStage as s -> s
            | :? string as s -> PlanStage.ofString s |> Option.defaultValue PlanStage.S1
            | _ -> PlanStage.S1

        PlanEvents.incumbencyOpened workId incumbencyId stage openingCursor

    let incumbencyRetired (incumbencyId: string) (outcomeVal: obj) (retirementCursor: int64) : PlanEvent =
        let outcome =
            match outcomeVal with
            | :? PlanRetirementOutcome as o -> o
            | :? string as s ->
                PlanRetirementOutcome.ofString s
                |> Option.defaultValue PlanRetirementOutcome.Continue
            | _ -> PlanRetirementOutcome.Continue

        PlanEvents.incumbencyRetired incumbencyId outcome retirementCursor

    let delivered (incumbencyId: string) (workId: string) (digest: string) (path: string) : PlanEvent =
        PlanEvents.delivered incumbencyId workId digest path

    let askPending (workId: string) (incumbencyId: string) (question: string) (cursor: int64) : PlanEvent =
        PlanEvents.askPending workId incumbencyId question cursor

    let askResolved (incumbencyId: string) (cursor: int64) : PlanEvent =
        PlanEvents.askResolved incumbencyId cursor

    let applyWorkEvent (event: PlanEvent) (state: PlanWorkState) : obj = PlanFold.applyWorkEventJs event state

    let activeStage (state: PlanWorkState) : string option =
        match state.Active with
        | Some act -> Some(PlanStage.render act.Stage)
        | None -> None

    let retiredCount (state: PlanWorkState) : int = state.Retired.Length

    let latestRetirementRange (state: PlanWorkState) : (int64 * int64) option =
        state.LatestRetirement
        |> Option.map (fun (_, _, startC, endC) -> (XTraceCursor.sequence startC, XTraceCursor.sequence endC))

    let isDelivered (state: PlanWorkState) : bool = state.Delivered.IsSome

    let deliveredDigest (state: PlanWorkState) : string option =
        match state.Delivered with
        | Some d -> Some d.Digest
        | None -> None

    let deliveryReceipt (state: PlanWorkState) : PlanDeliveryReceipt option = state.Delivered

    let isActionAllowed (request: obj) : bool = ActionGate.isActionAllowed request

    let private rejectionCode (rejection: ActionGateRejection) : string =
        match rejection with
        | ActionGateRejection.AlreadyDelivered -> "AlreadyDelivered"
        | ActionGateRejection.DeliverForbiddenInS1 -> "DeliverForbiddenInS1"
        | ActionGateRejection.HandoffForbiddenInS3 -> "HandoffForbiddenInS3"
        | ActionGateRejection.BlockedByLiveResources _ -> "BlockedByLiveResources"
        | ActionGateRejection.ActionDenied _ -> "ActionDenied"

    let decideAction (stageStr: string) (action: string) (hasDelivered: bool) (hasBlocker: bool) : obj =
        match PlanStage.ofString stageStr with
        | Some stage ->
            match ActionGate.decideAction stage action hasDelivered hasBlocker with
            | Ok() -> box {| ok = true; rejection = null |}
            | Error rej ->
                box
                    {| ok = false
                       rejection = rejectionCode rej |}
        | None ->
            box
                {| ok = false
                   rejection = "InvalidStage" |}

    let allowedPermissions (stageStr: string) (hasDelivered: bool) (hasBlocker: bool) : string array =
        match PlanStage.ofString stageStr with
        | Some stage ->
            ActionGate.allowedPermissions stage hasDelivered hasBlocker
            |> Set.toArray
            |> Array.choose (function
                | ToolPermission.JsPlan -> Some "JsPlan"
                | ToolPermission.Ask -> Some "Ask"
                | ToolPermission.Resume -> Some "Resume"
                | ToolPermission.Handoff -> Some "Handoff"
                | ToolPermission.Deliver -> Some "Deliver"
                | _ -> None)
            |> Array.sort
        | None -> [||]

    let encodeEventJson (event: PlanEvent) : string =
        let payload = PlanEventCodec.encodePayload event
        Wanxiangshu.Foundation.CanonicalJson.canonicalJson payload

    let decodeEventJson (eventType: string) (json: string) : obj =
        match Decode.fromString Decode.value json with
        | Ok payload ->
            match PlanEventCodec.decodePayload eventType payload with
            | Ok ev ->
                box
                    {| ok = true
                       event = ev
                       error = null |}
            | Error err ->
                box
                    {| ok = false
                       event = Unchecked.defaultof<PlanEvent>
                       error = err |}
        | Error parseErr ->
            box
                {| ok = false
                   event = Unchecked.defaultof<PlanEvent>
                   error = sprintf "%A" parseErr |}

    let toCanonicalEnvelopeJson (workId: string) (event: PlanEvent) (parents: string list) : string =
        let parentIds = parents |> List.map EventId.create
        let envelope = PlanEventCodec.toEnvelope (PlanWorkId.create workId) event parentIds
        PlanEventCodec.encodeCanonicalJson envelope

    let private unpackEventDetails (ev: PlanEvent) =
        match ev with
        | PlanEvent.PlanWorkOpened(wid, root) -> (PlanEventTypes.WorkOpened, PlanWorkId.value wid, null, null, 0L, root)
        | PlanEvent.PlanDevOpsBound(wid, devopsId, _) ->
            (PlanEventTypes.DevOpsBound, PlanWorkId.value wid, null, null, 0L, devopsId)
        | PlanEvent.PlanIncumbencyOpened(wid, iid, st, cur) ->
            (PlanEventTypes.IncumbencyOpened,
             PlanWorkId.value wid,
             PlanIncumbencyId.value iid,
             PlanStage.render st,
             XTraceCursor.sequence cur,
             null)
        | PlanEvent.PlanIncumbencyRetired(iid, outc, cur) ->
            (PlanEventTypes.IncumbencyRetired,
             null,
             PlanIncumbencyId.value iid,
             PlanRetirementOutcome.render outc,
             XTraceCursor.sequence cur,
             null)
        | PlanEvent.PlanDelivered(iid, wid, dig, p) ->
            (PlanEventTypes.Delivered, PlanWorkId.value wid, PlanIncumbencyId.value iid, dig, 0L, p)
        | PlanEvent.PlanAskPending(wid, iid, q, cur) ->
            (PlanEventTypes.AskPending,
             PlanWorkId.value wid,
             PlanIncumbencyId.value iid,
             q,
             XTraceCursor.sequence cur,
             null)
        | PlanEvent.PlanAskResolved(iid, cur) ->
            (PlanEventTypes.AskResolved, null, PlanIncumbencyId.value iid, null, XTraceCursor.sequence cur, null)

    let tryDecodeEnvelopeJson (envelopeJson: string) : obj =
        let textWithLf =
            if envelopeJson.EndsWith("\n") then
                envelopeJson
            else
                envelopeJson + "\n"

        match CanonicalEventCodec.tryDecode textWithLf with
        | Ok envelope ->
            match PlanEventCodec.tryDecodeEnvelope envelope with
            | Ok ev ->
                let (evType, wId, iId, st, cur, _) = unpackEventDetails ev

                box
                    {| ok = true
                       event = ev
                       eventType = evType
                       workId = wId
                       incumbencyId = iId
                       stage = st
                       cursor = cur
                       error = null |}
            | Error err ->
                box
                    {| ok = false
                       event = Unchecked.defaultof<PlanEvent>
                       eventType = null
                       workId = null
                       incumbencyId = null
                       stage = null
                       cursor = 0L
                       error = err |}
        | Error _ ->
            try
                let parsed: obj = emitJsExpr envelopeJson "JSON.parse($0)"

                let eventType: string =
                    let et = emitJsExpr parsed "$0.event_type || $0.EventType"
                    if isNull et then "" else unbox<string> et

                let payload: JsonValue =
                    emitJsExpr
                        parsed
                        "$0.payload !== undefined ? $0.payload : ($0.Payload !== undefined ? $0.Payload : $0)"

                if String.IsNullOrEmpty eventType then
                    box
                        {| ok = false
                           event = Unchecked.defaultof<PlanEvent>
                           eventType = null
                           workId = null
                           incumbencyId = null
                           stage = null
                           cursor = 0L
                           error = "Missing event_type or EventType in envelope JSON" |}
                else
                    match PlanEventCodec.decodePayload eventType payload with
                    | Ok ev ->
                        let (evType, wId, iId, st, cur, _) = unpackEventDetails ev

                        box
                            {| ok = true
                               event = ev
                               eventType = evType
                               workId = wId
                               incumbencyId = iId
                               stage = st
                               cursor = cur
                               error = null |}
                    | Error err ->
                        box
                            {| ok = false
                               event = Unchecked.defaultof<PlanEvent>
                               eventType = null
                               workId = null
                               incumbencyId = null
                               stage = null
                               cursor = 0L
                               error = err |}
            with ex ->
                box
                    {| ok = false
                       event = Unchecked.defaultof<PlanEvent>
                       eventType = null
                       workId = null
                       incumbencyId = null
                       stage = null
                       cursor = 0L
                       error = ex.Message |}

    let append (store: IEventStore) (workId: string) (event: PlanEvent) : Task<obj> =
        task {
            match! PlanEventStore.append store (PlanWorkId.create workId) event with
            | Ok eventId ->
                return
                    box
                        {| ok = true
                           eventId = EventId.value eventId
                           error = null |}
            | Error err ->
                return
                    box
                        {| ok = false
                           eventId = null
                           error = err |}
        }

    let getPlanWorkState (store: IEventStore) (workId: string) : obj =
        match PlanEventStore.workState store (PlanWorkId.create workId) with
        | Some st -> box {| found = true; state = st |}
        | None ->
            box
                {| found = false
                   state = Unchecked.defaultof<PlanWorkState> |}

    let getPlanWorkView (store: IEventStore) (workId: string) : obj =
        match PlanEventStore.view store (PlanWorkId.create workId) with
        | Some v -> box {| found = true; view = v |}
        | None ->
            box
                {| found = false
                   view = Unchecked.defaultof<PlanWorkView> |}

    let getPlanWorkStateFromIntegrator (integrator: ICanonicalIntegrator) (workId: string) : obj =
        match PlanEventStore.workStateFromIntegrator integrator (PlanWorkId.create workId) with
        | Some st -> box {| found = true; state = st |}
        | None ->
            box
                {| found = false
                   state = Unchecked.defaultof<PlanWorkState> |}

    let getPlanWorkViewFromIntegrator (integrator: ICanonicalIntegrator) (workId: string) : obj =
        match PlanEventStore.viewFromIntegrator integrator (PlanWorkId.create workId) with
        | Some v -> box {| found = true; view = v |}
        | None ->
            box
                {| found = false
                   view = Unchecked.defaultof<PlanWorkView> |}

    let assembleTenureMessages (messages: obj) (tenureObj: obj) (materializePrev: obj -> string) : obj =
        TenureIsolation.assembleTenureMessages messages tenureObj materializePrev

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
        if isNull v then
            None
        elif emitJsExpr v "Array.isArray($0)" then
            let arr = unbox<obj array> v

            if arr.Length = 0 || isNull arr.[0] then
                None
            else
                let x = arr.[0]

                if emitJsExpr x "typeof $0 === 'bigint'" then
                    Some(unbox<int64> x)
                elif emitJsExpr x "typeof $0 === 'number'" then
                    Some(int64 (unbox<int> x))
                else
                    None
        elif emitJsExpr v "typeof $0 === 'bigint'" then
            Some(unbox<int64> v)
        elif emitJsExpr v "typeof $0 === 'number'" then
            Some(int64 (unbox<int> v))
        else
            None

    let executeHandoff
        (store: IEventStore)
        (workId: string)
        (incumbencyId: string)
        (hasBlocker: bool)
        (note: obj)
        (retirementCursor: obj)
        : Task<obj> =
        let noteOpt = extractStringOpt note
        let retCursorOpt = extractInt64Opt retirementCursor

        task {
            match! PlanTools.executeHandoff store workId incumbencyId hasBlocker noteOpt retCursorOpt with
            | Ok res ->
                return
                    box
                        {| ok = true
                           retiredIncumbencyId = res.RetiredIncumbencyId
                           nextIncumbencyId = res.NextIncumbencyId
                           nextStage = res.NextStage
                           message = res.Message
                           error = null |}
            | Error err ->
                return
                    box
                        {| ok = false
                           retiredIncumbencyId = null
                           nextIncumbencyId = null
                           nextStage = null
                           message = null
                           error = err |}
        }

    let executeDeliver
        (store: IEventStore)
        (root: string)
        (workId: string)
        (incumbencyId: string)
        (hasBlocker: bool)
        (note: obj)
        (retirementCursor: obj)
        : Task<obj> =
        let noteOpt = extractStringOpt note
        let retCursorOpt = extractInt64Opt retirementCursor

        task {
            match! PlanTools.executeDeliver store root workId incumbencyId hasBlocker noteOpt retCursorOpt with
            | Ok res ->
                return
                    box
                        {| ok = true
                           delivered = res.Ok
                           digest = res.Digest
                           path = res.Path
                           message = res.Message
                           error = null |}
            | Error err ->
                return
                    box
                        {| ok = false
                           delivered = false
                           digest = null
                           path = null
                           message = null
                           error = err |}
        }

    let executeAsk (store: IEventStore) (workId: string) (question: string) : obj =
        match PlanTools.executeAsk store workId question with
        | Ok res ->
            box
                {| ok = true
                   kind = res.Kind
                   question = res.Question
                   status = res.Status
                   error = null |}
        | Error err ->
            box
                {| ok = false
                   kind = null
                   question = null
                   status = null
                   error = err |}

    let executeAskAsync (store: IEventStore) (workId: string) (question: string) : Task<obj> =
        Task.FromResult(executeAsk store workId question)

    let assembleAskContinuation (messages: obj) (pendingAskObj: obj) (tenureObj: obj) : obj =
        TenureIsolation.assembleAskContinuation messages pendingAskObj tenureObj

    let executeResume (store: IEventStore) (workId: string) (charge: string) (name: obj) : Task<obj> =
        let nameOpt = extractStringOpt name

        task {
            match! PlanTools.executeResume store workId charge nameOpt with
            | Ok res ->
                return
                    box
                        {| ok = true
                           status = res.Status
                           target = res.Target
                           charge = res.Charge
                           error = null |}
            | Error err ->
                return
                    box
                        {| ok = false
                           status = null
                           target = null
                           charge = null
                           error = err |}
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

    let private extractPlanWorkView (viewVal: obj) : PlanWorkView option =
        match viewVal with
        | :? PlanWorkView as v -> Some v
        | null -> None
        | _ ->
            try
                let wid =
                    if isNull viewVal?WorkId then
                        None
                    else
                        Some(unbox<string> viewVal?WorkId)

                let activeInc =
                    if isNull viewVal?ActiveIncumbencyId then
                        None
                    else
                        Some(unbox<string> viewVal?ActiveIncumbencyId)

                let activeStage =
                    if isNull viewVal?ActiveStage then
                        None
                    else
                        Some(unbox<string> viewVal?ActiveStage)

                let activePhase =
                    if isNull viewVal?ActivePhase then
                        None
                    else
                        Some(unbox<string> viewVal?ActivePhase)

                let retiredCount =
                    if isNull viewVal?RetiredCount then
                        0
                    else
                        unbox<int> viewVal?RetiredCount

                let latestRetOutcome =
                    if isNull viewVal?LatestRetirementOutcome then
                        None
                    else
                        Some(unbox<string> viewVal?LatestRetirementOutcome)

                let delivered =
                    if isNull viewVal?Delivered then
                        false
                    else
                        unbox<bool> viewVal?Delivered

                let deliveryDigest =
                    if isNull viewVal?DeliveryDigest then
                        None
                    else
                        Some(unbox<string> viewVal?DeliveryDigest)

                let deliveryPath =
                    if isNull viewVal?DeliveryPath then
                        None
                    else
                        Some(unbox<string> viewVal?DeliveryPath)

                let boundDevOpsId =
                    if isNull viewVal?BoundDevOpsId then
                        None
                    else
                        Some(unbox<string> viewVal?BoundDevOpsId)

                let pendingAskQ =
                    if isNull viewVal?PendingAskQuestion then
                        None
                    else
                        Some(unbox<string> viewVal?PendingAskQuestion)

                let pendingAskInc =
                    if isNull viewVal?PendingAskIncumbencyId then
                        None
                    else
                        Some(unbox<string> viewVal?PendingAskIncumbencyId)

                let pendingAskCur =
                    if isNull viewVal?PendingAskCursor then
                        None
                    else
                        Some(unbox<int64> viewVal?PendingAskCursor)

                Some
                    { WorkId = wid
                      ActiveIncumbencyId = activeInc
                      ActiveStage = activeStage
                      ActivePhase = activePhase
                      RetiredCount = retiredCount
                      LatestRetirementOutcome = latestRetOutcome
                      Delivered = delivered
                      DeliveryDigest = deliveryDigest
                      DeliveryPath = deliveryPath
                      BoundDevOpsId = boundDevOpsId
                      PendingAskQuestion = pendingAskQ
                      PendingAskIncumbencyId = pendingAskInc
                      PendingAskCursor = pendingAskCur }
            with _ ->
                None

    let planRecoveryPosition (viewVal: obj) (readPlanFile: string -> string option) : obj =
        let viewOpt = extractPlanWorkView viewVal

        match viewOpt with
        | None ->
            box
                {| kind = "Conflict"
                   reason = "Invalid PlanWorkView input" |}
        | Some view ->
            match PlanRecovery.planRecoveryPosition view readPlanFile with
            | PlanRecoveryPosition.Delivered(receipt, path, digest) ->
                box
                    {| kind = "Delivered"
                       receipt = receipt
                       path = path
                       digest = digest |}
            | PlanRecoveryPosition.Active(incumbencyId, stage, planExists) ->
                box
                    {| kind = "Active"
                       incumbencyId = incumbencyId
                       stage = stage
                       planExists = planExists |}
            | PlanRecoveryPosition.Nothing -> box {| kind = "Nothing" |}
            | PlanRecoveryPosition.Conflict reason -> box {| kind = "Conflict"; reason = reason |}

    let private parseViews (v: obj) : PlanWorkView list =
        if isNull v then
            []
        elif emitJsExpr v "Array.isArray($0)" then
            let arr = unbox<obj array> v

            arr
            |> Array.toList
            |> List.map (fun item ->
                extractPlanWorkView item
                |> Option.defaultValue
                    { WorkId = None
                      ActiveIncumbencyId = None
                      ActiveStage = None
                      ActivePhase = None
                      RetiredCount = 0
                      LatestRetirementOutcome = None
                      Delivered = false
                      DeliveryDigest = None
                      DeliveryPath = None
                      BoundDevOpsId = None
                      PendingAskQuestion = None
                      PendingAskIncumbencyId = None
                      PendingAskCursor = None })
        else
            match v with
            | :? (PlanWorkView list) as l -> l
            | :? (PlanWorkView array) as arr -> Array.toList arr
            | _ -> []

    let evaluateRecoveryEffects (viewsVal: obj) (readPlanFile: string -> string option) : obj array =
        let views = parseViews viewsVal
        let effects = PlanRecovery.evaluateRecoveryEffects views readPlanFile

        effects
        |> List.map (function
            | PlanRecoveryEffect.DeliveredIdempotent(workId, path) ->
                box
                    {| kind = "DeliveredIdempotent"
                       workId = workId
                       path = path |}
            | PlanRecoveryEffect.ActiveRebound(workId, incumbencyId, stage, planExists) ->
                box
                    {| kind = "ActiveRebound"
                       workId = workId
                       incumbencyId = incumbencyId
                       stage = stage
                       planExists = planExists |}
            | PlanRecoveryEffect.Conflict(workId, reason) ->
                box
                    {| kind = "Conflict"
                       workId = workId
                       reason = reason |})
        |> List.toArray

    let allWorkViews (store: IEventStore) : PlanWorkView list = PlanEventStore.allWorkViews store

    let allWorkViewsFromIntegrator (integrator: ICanonicalIntegrator) : PlanWorkView list =
        PlanEventStore.allWorkViewsFromIntegrator integrator

    let PlanStage: obj =
        box
            {| S1 = PlanStage.S1
               S2 = PlanStage.S2
               S3 = PlanStage.S3 |}

    let PlanRetirementOutcome: obj =
        box
            {| Continue = PlanRetirementOutcome.Continue
               Delivered = PlanRetirementOutcome.Delivered |}

    let PlanEvents: obj =
        box
            {| workOpened = workOpened
               devOpsBound = devOpsBound
               incumbencyOpened = incumbencyOpened
               incumbencyRetired = incumbencyRetired
               delivered = delivered
               askPending = askPending
               askResolved = askResolved |}

    let PlanFold: obj =
        box
            {| initialWorkState = Wanxiangshu.Mission.Planning.PlanFold.initialWorkState
               initialState = Wanxiangshu.Mission.Planning.PlanFold.initialState
               applyWorkEventJs =
                fun (ev: PlanEvent) (st: PlanWorkState) -> Wanxiangshu.Mission.Planning.PlanFold.applyWorkEventJs ev st
               applyEventJs =
                fun (ev: PlanEvent) (st: PlanState) -> Wanxiangshu.Mission.Planning.PlanFold.applyEventJs ev st
               applyEventsJs =
                fun (evs: PlanEvent list) (st: PlanState) -> Wanxiangshu.Mission.Planning.PlanFold.applyEventsJs evs st
               workState = fun (wid: string) (st: PlanState) -> Wanxiangshu.Mission.Planning.PlanFold.workState wid st
               view = fun (wid: string) (st: PlanState) -> Wanxiangshu.Mission.Planning.PlanFold.view wid st |}

    let PlanningSurface: obj =
        box
            {| empty = empty
               initialWorkState = Wanxiangshu.Mission.Planning.PlanFold.initialWorkState
               applyWorkEvent = applyWorkEvent
               encodeEventJson = encodeEventJson
               decodeEventJson = decodeEventJson
               toCanonicalEnvelopeJson = toCanonicalEnvelopeJson
               tryDecodeEnvelopeJson = tryDecodeEnvelopeJson
               append = append
               getPlanWorkState = getPlanWorkState
               getPlanWorkView = getPlanWorkView
               getPlanWorkStateFromIntegrator = getPlanWorkStateFromIntegrator
               getPlanWorkViewFromIntegrator = getPlanWorkViewFromIntegrator
               assembleTenureMessages = assembleTenureMessages
               validateWorkId = validateWorkId
               canonicalWorkKey = canonicalWorkKey
               resolvePlanPath = resolvePlanPath
               readPlan = readPlan
               rewritePlanAtomic = rewritePlanAtomic
               editPlanAtomic = editPlanAtomic
               executeHandoff = executeHandoff
               executeDeliver = executeDeliver
               executeAsk = executeAsk
               executeAskAsync = executeAskAsync
               assembleAskContinuation = assembleAskContinuation
               executeResume = executeResume
               executeJsPlan = executeJsPlan
               planRecoveryPosition = planRecoveryPosition
               evaluateRecoveryEffects = evaluateRecoveryEffects
               allWorkViews = allWorkViews
               allWorkViewsFromIntegrator = allWorkViewsFromIntegrator
               latestRetirementRange = latestRetirementRange
               workOpened = workOpened
               devOpsBound = devOpsBound
               incumbencyOpened = incumbencyOpened
               incumbencyRetired = incumbencyRetired
               delivered = delivered
               askPending = askPending
               askResolved = askResolved
               PlanRetirementOutcome = PlanRetirementOutcome
               PlanStage = PlanStage |}
