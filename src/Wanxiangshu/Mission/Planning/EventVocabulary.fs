namespace Wanxiangshu.Mission.Planning

module PlanEventTypes =
    let WorkOpened = "PlanWorkOpened"
    let DevOpsBound = "PlanDevOpsBound"
    let IncumbencyOpened = "PlanIncumbencyOpened"
    let IncumbencyRetired = "PlanIncumbencyRetired"
    let Delivered = "PlanDelivered"
    let AskPending = "PlanAskPending"
    let AskResolved = "PlanAskResolved"

    let all =
        [ WorkOpened
          DevOpsBound
          IncumbencyOpened
          IncumbencyRetired
          Delivered
          AskPending
          AskResolved ]

    let isPlanEvent (eventType: string) : bool = all |> List.contains eventType
