namespace Wanxiangshu.Repository.Investigation.Semble

open System
open System.Threading.Tasks
open Fable.Core.JsInterop

/// AGENT-027: internal Semble search. Not Host mcp.
module SembleMcpClient =

    let private variableText (vars: obj) name =
        if isNull vars then
            None
        else
            vars?(name)
            |> Option.ofObj
            |> Option.map string
            |> Option.filter (String.IsNullOrWhiteSpace >> not)

    let launchFromVars (vars: obj) : SembleMcp.Launch =
        SembleMcp.launchFrom (variableText vars)

    let launchFromEnvironment () : SembleMcp.Launch =
        SembleMcp.launchFrom (fun name ->
            match Environment.GetEnvironmentVariable name with
            | null
            | "" -> None
            | value -> Some value)

    let private invocation (launch: SembleMcp.Launch) : (string * string array) option =
        match launch with
        | SembleMcp.Launch.Disabled -> None
        | SembleMcp.Launch.Fixture path ->
            let cmd = SembleMcp.fixtureCommand path
            Some(cmd.[0], cmd.[1..])
        | SembleMcp.Launch.Uvx gitRef ->
            let cmd = SembleMcp.uvxCommand gitRef
            Some(cmd.[0], cmd.[1..])

    let private decodeSearchTool command args toolArgs : Task<SembleMcp.Hit list> =
        task {
            match! SembleMcpStdio.callTool command args SembleMcp.toolName toolArgs 15000 with
            | None -> return []
            | Some raw -> return SembleSearchCodec.parseToolResult raw
        }

    let private invokeSearch command args query repoPath topK : Task<SembleMcp.Hit list> =
        task {
            if String.IsNullOrWhiteSpace query || String.IsNullOrWhiteSpace repoPath then
                return []
            else
                let k = if topK < 1 then 1 else topK

                let toolArgs =
                    createObj
                        [ "query" ==> query
                          "repo" ==> repoPath
                          "top_k" ==> k
                          "max_snippet_lines" ==> SembleMcp.maxSnippetLines ]

                return! decodeSearchTool command args toolArgs
        }

    let search (launch: SembleMcp.Launch) (query: string) (repoPath: string) (topK: int) : Task<SembleMcp.Hit list> =
        task {
            match invocation launch with
            | None -> return []
            | Some(command, args) -> return! invokeSearch command args query repoPath topK
        }

    let searchFromEnvironment (query: string) (repoPath: string) (topK: int) =
        search (launchFromEnvironment ()) query repoPath topK
