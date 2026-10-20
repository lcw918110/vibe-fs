namespace Wanxiangshu.Composition.Durable

open Wanxiangshu.Foundation.Identity
open Wanxiangshu.Host
open Wanxiangshu.OpenCode.Host.PairProgramming
open Wanxiangshu.Execution.Session
open Wanxiangshu.Foundation

module HostFactFold =

    let private reject = FoldRejection.reject

    let private guidelineRejection =
        function
        | GuidelineFoldRejection.NonSequentialOrdinal(expected, actual) ->
            { Fact = "PairProgrammingGuidelineAnchored"
              Reason = sprintf "ordinal %d is not the successor of %d (HOST-013)" actual expected }
        | GuidelineFoldRejection.DuplicateCallId callId ->
            { Fact = "PairProgrammingGuidelineAnchored"
              Reason = sprintf "call id %s already exists in this transcript (HOST-013)" callId }
        | GuidelineFoldRejection.DuplicatePlacement(callGap, resultGap) ->
            { Fact = "PairProgrammingGuidelineAnchored"
              Reason = sprintf "placement (%A, %A) already exists in this transcript (HOST-013 §8)" callGap resultGap }

    let fold (projection: AgentProjectionSet) (fact: HostFactCases) : Result<AgentProjectionSet, FoldRejection> =
        match fact with
        | HostFactCases.PairProgrammingGuidelineAnchored payload ->
            AgentProjection.tryUpdate
                payload.SessionId
                (fun session ->
                    session.Guidelines
                    |> Option.defaultValue GuidelineProjection.empty
                    |> GuidelineProjection.apply
                        payload.Ordinal
                        payload.CallId
                        payload.MarkerText
                        payload.CallGap
                        payload.ResultGap
                    |> Result.map (fun updated ->
                        { session with
                            Guidelines = Some updated }))
                projection
            |> Result.mapError guidelineRejection

        | HostFactCases.SessionStartedAtBound payload ->
            Ok(
                AgentProjection.update
                    payload.SessionId
                    (fun session ->
                        { session with
                            SessionStartedAt =
                                Some(SessionStartedAtProjection.bind payload.StartedAt session.SessionStartedAt) })
                    projection
            )
