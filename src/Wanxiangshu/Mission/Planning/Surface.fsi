namespace Wanxiangshu.Mission.Planning

open System.Threading.Tasks
open Wanxiangshu.Persistence.EventStore

module PlanningSurface =
    val empty: unit -> PlanWorkState
    val workOpened: workId: string -> root: string -> PlanEvent
    val devOpsBound: workId: string -> devopsId: string -> targetVal: obj -> PlanEvent
    val incumbencyOpened: workId: string -> incumbencyId: string -> stageVal: obj -> openingCursor: int64 -> PlanEvent
    val incumbencyRetired: incumbencyId: string -> outcomeVal: obj -> retirementCursor: int64 -> PlanEvent
    val delivered: incumbencyId: string -> workId: string -> digest: string -> path: string -> PlanEvent
    val askPending: workId: string -> incumbencyId: string -> question: string -> cursor: int64 -> PlanEvent
    val askResolved: incumbencyId: string -> cursor: int64 -> PlanEvent
    val applyWorkEvent: event: PlanEvent -> state: PlanWorkState -> obj
    val latestRetirementRange: state: PlanWorkState -> (int64 * int64) option
    val activeStage: state: PlanWorkState -> string option
    val retiredCount: state: PlanWorkState -> int
    val isDelivered: state: PlanWorkState -> bool
    val deliveredDigest: state: PlanWorkState -> string option
    val deliveryReceipt: state: PlanWorkState -> PlanDeliveryReceipt option
    val isActionAllowed: request: obj -> bool
    val decideAction: stageStr: string -> action: string -> hasDelivered: bool -> hasBlocker: bool -> obj
    val allowedPermissions: stageStr: string -> hasDelivered: bool -> hasBlocker: bool -> string array
    val encodeEventJson: event: PlanEvent -> string
    val decodeEventJson: eventType: string -> json: string -> obj
    val toCanonicalEnvelopeJson: workId: string -> event: PlanEvent -> parents: string list -> string
    val tryDecodeEnvelopeJson: envelopeJson: string -> obj
    val append: store: IEventStore -> workId: string -> event: PlanEvent -> Task<obj>
    val getPlanWorkState: store: IEventStore -> workId: string -> obj
    val getPlanWorkView: store: IEventStore -> workId: string -> obj
    val getPlanWorkStateFromIntegrator: integrator: ICanonicalIntegrator -> workId: string -> obj
    val getPlanWorkViewFromIntegrator: integrator: ICanonicalIntegrator -> workId: string -> obj
    val assembleTenureMessages: messages: obj -> tenureObj: obj -> materializePrev: (obj -> string) -> obj
    val validateWorkId: workId: string -> obj
    val canonicalWorkKey: workId: string -> obj
    val resolvePlanPath: root: string -> workId: string -> obj
    val readPlan: root: string -> workId: string -> obj
    val rewritePlanAtomic: root: string -> workId: string -> content: string -> obj
    val editPlanAtomic: root: string -> workId: string -> patches: obj -> obj

    val executeHandoff:
        store: IEventStore ->
        workId: string ->
        incumbencyId: string ->
        hasBlocker: bool ->
        note: obj ->
        retirementCursor: obj ->
            Task<obj>

    val executeDeliver:
        store: IEventStore ->
        root: string ->
        workId: string ->
        incumbencyId: string ->
        hasBlocker: bool ->
        note: obj ->
        retirementCursor: obj ->
            Task<obj>

    val executeAsk: store: IEventStore -> workId: string -> question: string -> obj
    val executeAskAsync: store: IEventStore -> workId: string -> question: string -> Task<obj>
    val assembleAskContinuation: messages: obj -> pendingAskObj: obj -> tenureObj: obj -> obj
    val executeResume: store: IEventStore -> workId: string -> charge: string -> name: obj -> Task<obj>
    val executeJsPlan: root: string -> workId: string -> action: string -> content: obj -> patches: obj -> obj
    val planRecoveryPosition: viewVal: obj -> readPlanFile: (string -> string option) -> obj
    val evaluateRecoveryEffects: viewsVal: obj -> readPlanFile: (string -> string option) -> obj array
    val allWorkViews: store: IEventStore -> PlanWorkView list
    val allWorkViewsFromIntegrator: integrator: ICanonicalIntegrator -> PlanWorkView list
    val PlanStage: obj
    val PlanRetirementOutcome: obj
    val PlanEvents: obj
    val PlanFold: obj
    val PlanningSurface: obj
