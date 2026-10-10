namespace Wanxiangshu.Mission.Planning

open System.Threading.Tasks
open Wanxiangshu.Foundation.Identity
open Wanxiangshu.Persistence.EventStore

[<RequireQualifiedAccess>]
module PlanEventStore =
    val append: store: IEventStore -> workId: PlanWorkId -> event: PlanEvent -> Task<Result<EventId, string>>
    val currentPlanState: store: IEventStore -> PlanState option
    val workState: store: IEventStore -> workId: PlanWorkId -> PlanWorkState option
    val view: store: IEventStore -> workId: PlanWorkId -> PlanWorkView option
    val allWorkViews: store: IEventStore -> PlanWorkView list
    val workStateFromIntegrator: integrator: ICanonicalIntegrator -> workId: PlanWorkId -> PlanWorkState option
    val viewFromIntegrator: integrator: ICanonicalIntegrator -> workId: PlanWorkId -> PlanWorkView option
    val allWorkViewsFromIntegrator: integrator: ICanonicalIntegrator -> PlanWorkView list
    val tryActiveWorkState: tryCurrent: (string -> obj option) -> PlanWorkState option
