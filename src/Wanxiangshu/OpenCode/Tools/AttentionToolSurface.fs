namespace Wanxiangshu.OpenCode

open System.Threading.Tasks
open Wanxiangshu.Foundation.Identity
open Wanxiangshu.Interaction.Attention

[<RequireQualifiedAccess>]
module AttentionToolSurface =

    type private ToolsHandle(specs: ToolSpec list) =
        member _.Specs = specs

    let private createTools toolModule port =
        ToolsHandle(AttentionTools.specs (ToolHostCodec.factory toolModule) port) :> obj

    let create (toolModule: obj) (snapshot: unit -> obj) (append: string -> string -> obj -> Task<bool>) =
        let appendFact sessionId providerRun fact =
            task {
                match fact with
                | AttentionFactCases.DeferredWorkConsumed _ ->
                    // Consumption receipts are appended by their owning carriers;
                    // this test surface only drives the `defer` tool.
                    return Ok()
                | AttentionFactCases.DeferredWorkRecorded work ->
                    let! accepted =
                        append
                            (SessionId.value sessionId)
                            (providerRun |> Option.map ProviderRunIdentity.value |> Option.toObj)
                            (box
                                {| session = SessionId.value work.SessionId
                                   occurrence = work.OccurrenceId
                                   text = work.Text |})

                    return
                        if accepted then
                            Ok()
                        else
                            Error AttentionAppendFailure.DurabilityUnavailable
            }

        let port =
            { Read = fun () -> snapshot () |> AttentionSurface.projection
              Append = appendFact }

        createTools toolModule (Some port)

    let withoutJournal (toolModule: obj) = createTools toolModule None

    let execute (tools: obj) name (arguments: obj) (context: obj) =
        let handle = unbox<ToolsHandle> tools
        let spec = handle.Specs |> List.find (fun spec -> spec.Name = name)
        spec.Execute (HostToolArguments arguments) (ToolHostCodec.decodeContext context)
