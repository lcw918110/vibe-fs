namespace Wanxiangshu.Mission.Planning

open Wanxiangshu.Persistence.EventStore

[<RequireQualifiedAccess>]
module PlanIntegrationRules =
    val planRule: IntegrationRule
    val rules: IntegrationRule list
