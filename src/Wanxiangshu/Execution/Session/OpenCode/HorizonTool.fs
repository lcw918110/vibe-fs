namespace Wanxiangshu.Execution.Session.OpenCode

open System
open System.Threading.Tasks
open Fable.Core.JsInterop
open Wanxiangshu.Execution.Delegation.Fork
open Wanxiangshu.Execution.Delegation.Fork.Host
open Wanxiangshu.Execution.Session
open Wanxiangshu.Foundation.Identity
open Wanxiangshu.Host
open Wanxiangshu.Host.Domain
open Wanxiangshu.Host.Ports
open Wanxiangshu.OpenCode
open Wanxiangshu.Participant.Provider
open Wanxiangshu.Persistence.Journal

/// OpenCode adapter for horizon(): binds `DelegationEnvironment` to one tool
/// call and puts `HorizonRoster` on the OpenCode wire.
module HorizonTool =
    type HorizonRuntimeContext =
        { RuntimeFor: HostToolContext -> Result<HostForkRuntime, string>
          LogicalOwnerFor: SessionId -> SessionId
          Journal: AgentJournal option
          EnsureWorkDevOpsBound: SessionId -> Task<unit> }

    let private environment (runtimeCtx: HorizonRuntimeContext) (context: HostToolContext) : DelegationEnvironment =
        { Caller =
            { Session =
                if String.IsNullOrWhiteSpace context.SessionId then
                    None
                else
                    Some(SessionId.create context.SessionId)
              ProviderRunId = context.ProviderRunId
              ToolCallId = context.ToolCallId }

          Journal = runtimeCtx.Journal

          ForkRuntime =
            fun () ->
                match runtimeCtx.RuntimeFor context with
                | Ok runtime -> Ok runtime
                | Error reason -> Error(HostError.Refused reason)

          LogicalOwnerFor = runtimeCtx.LogicalOwnerFor
          EnsureWorkDevOpsBound = runtimeCtx.EnsureWorkDevOpsBound
          WorkspaceDirectory = None
          ManagerCapabilityFactsFor = None
          CallerRole = None
          NewHandleId = None
          OpenManagerJob = (None: (ManagerJobId * string * string * string * int option -> HostResult<string>) option)
          ContinueManagerJob =
            (None: (ManagerJobId * string * ProviderRunIdentity * ToolCallId * int option -> HostResult<string>) option) }

    let private execute (runtimeCtx: HorizonRuntimeContext) (_args: HostToolArguments) context =
        task {
            let! lines = HorizonRoster.instructions (environment runtimeCtx context)
            return ToolResultText.tomlObjectWithInstructions lines []
        }


    let private pace (value: HorizonRoster.Pace) : obj =
        createObj
            [ "turnsUsed" ==> value.TurnsUsed
              "turnsLeft" ==> value.TurnsLeft
              "timeUsedMs" ==> value.TimeUsedMs
              "timeLeftMs" ==> value.TimeLeftMs ]

    /// One participant row: its Byname and standing, and the pace of a running
    /// charge only when it is known — an absent `pace` is never a row of zeros.
    let private participantRow (presence: HorizonRoster.Presence) : obj =
        match presence.Pace with
        | None ->
            createObj
                [ "label" ==> presence.Label
                  "status" ==> HorizonRoster.wireStatus presence.Standing ]
        | Some known ->
            createObj
                [ "label" ==> presence.Label
                  "status" ==> HorizonRoster.wireStatus presence.Standing
                  "pace" ==> pace known ]

    let private receiptOf (roster: HorizonRoster.Roster option) : obj =
        match roster with
        | None -> createObj [ "ok" ==> false; "code" ==> "horizon-unavailable" ]
        | Some roster ->
            createObj
                [ "ok" ==> true
                  "agents" ==> (roster.Participants |> List.map participantRow |> List.toArray)
                  "ptys"
                  ==> (roster.Terminals
                       |> List.map (fun terminal -> createObj [ "terminal" ==> terminal ])
                       |> List.toArray) ]

    let rosterReceipt (language: string) (runtimeCtx: HorizonRuntimeContext) (context: HostToolContext) : Task<obj> =
        task {
            let! roster = HorizonRoster.roster (ProviderLanguage.parse language) (environment runtimeCtx context)
            return receiptOf roster
        }

    let spec (runtimeCtx: HorizonRuntimeContext) =
        { Name = "horizon"
          Description =
            ProviderProse.render
                (ProviderLanguageBinding.readGlobalPreference ())
                HorizonRoster.Path.Description
                Map.empty
          Arguments = []
          Execute = execute runtimeCtx }
