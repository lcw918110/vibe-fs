module Wanxiangshu.Checker

open System
open System.IO
open System.Diagnostics
open System.Collections.Generic
open System.Security.Cryptography
open System.Text.Json
open System.Threading
open System.Runtime.InteropServices
open FSharp.Compiler.SourceCodeServices
open Fable
open Fable.Cli.Main
open Fable.Compiler.Util
open Fable.Transforms
open Fable.Transforms.State

[<DllImport("libc", EntryPoint = "kill")>]
extern int killGroup(int pid, int signal)

let checkers = Dictionary<string, InteractiveChecker>()
let order = Queue<string>()

let compile (request: JsonElement) = async {
    let total = Stopwatch.StartNew()
    let projectFile = request.GetProperty("project").GetString()
    let options = CompilerOptionsHelper.Make(language = JavaScript, typedArrays = true,
                    fileExtension = ".js", define = ["FABLE_COMPILER"; "FABLE_COMPILER_5"; "FABLE_COMPILER_JAVASCRIPT"],
                    debugMode = true, optimizeFSharpAst = false, noReflection = false, verbosity = Verbosity.Normal)
    let args: CliArgs = {
        ProjectFile = projectFile
        RootDir = request.GetProperty("root").GetString()
        OutDir = Some(request.GetProperty("output").GetString())
        IsWatch = false
        Precompile = false
        PrecompiledLib = None
        PrintAst = false
        FableLibraryPath = None
        Configuration = "Debug"
        NoRestore = request.GetProperty("noRestore").GetBoolean()
        NoCache = request.GetProperty("noCache").GetBoolean()
        NoGitignore = true
        NoParallelTypeCheck = false
        SourceMaps = false
        SourceMapsRoot = None
        Exclude = []
        Replace = Map.empty
        RunProcess = None
        CompilerOptions = options
        Verbosity = Verbosity.Normal
    }
    let project = ProjectCracked.Init(args)
    let referenceIdentity (option: string) =
        if option.StartsWith("-r:", StringComparison.Ordinal) then
            let file = option.Substring(3)
            "-r:" + Path.GetFileName(file) + ":" + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file)))
        else option
    let references () =
        project.ProjectOptions.OtherOptions
        |> Array.filter (fun option -> option.StartsWith("-r:", StringComparison.Ordinal))
        |> Array.map (fun option ->
            let file = option.Substring(3)
            {| path = file; hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file))).ToLowerInvariant() |})
    let referenceInputs = references ()
    // Fable supplies the current ordered sources to each check. Output staging paths
    // must not create another checker for the same project and compiler options.
    let key = JsonSerializer.Serialize((projectFile, project.ProjectOptions.OtherOptions |> Array.map referenceIdentity))
    let reused, checker =
        match checkers.TryGetValue(key) with
        | true, value -> true, value
        | _ ->
            if checkers.Count >= 4 then checkers.Remove(order.Dequeue()) |> ignore
            let value = InteractiveChecker.Create(project.ProjectOptions)
            checkers.Add(key, value)
            order.Enqueue(key)
            false, value
    do! checker.ClearCache()
    let! assemblies = checker.GetImportedAssemblies()
    let fableProject = Project.From(project.ProjectFile, project.ProjectOptions, [], assemblies, Log.log,
                            ?precompiledInfo = (project.PrecompiledInfo |> Option.map (fun value -> value :> _)),
                            getPlugin = Reflection.loadType args)
    let compiler = FableCompiler(checker, project, fableProject)
    let state = State.Create(args)
    let phase = Stopwatch.StartNew()
    let! diagnostics, results = compiler.StartCompilation(project.SourceFiles, project.SourceFilePaths,
                                    state.GetPathResolver(?precompiledInfo = project.PrecompiledInfo), false, fun _ -> false)
    let compileMs = phase.Elapsed.TotalMilliseconds
    let logs, failures =
        ((Array.toList diagnostics, []), results)
        ||> List.fold (fun (logs, failures) result ->
            match result with
            | Ok result -> (Array.toList result.Logs) @ logs, failures
            | Error result -> logs, (result.File + ": " + result.Exception.Message) :: failures)
    for entry in logs do
        if entry.Severity = Severity.Warning || entry.Severity = Severity.Error then
            eprintfn "%s: %A: %s" (defaultArg entry.FileName projectFile) entry.Severity entry.Message
    let errors =
        logs
        |> List.filter (fun entry ->
            entry.Severity = Severity.Error
            || (project.TreatmentWarningsAsErrors && entry.Severity = Severity.Warning
                && not (entry.FileName |> Option.exists Naming.isInFableModules)))
        |> List.map (fun entry -> entry.Message)
    do! checker.ClearCache()
    if referenceInputs <> references () then failwith "Referenced assemblies changed during compilation"
    return {| ok = errors.IsEmpty && failures.IsEmpty
              errors = errors @ failures
              references = referenceInputs
              reused = reused
              checkers = checkers.Count
              sources = project.SourceFiles.Length
              compileMs = compileMs
              totalMs = total.Elapsed.TotalMilliseconds
              pid = Environment.ProcessId
              memory = {| rss = Process.GetCurrentProcess().WorkingSet64
                          managedHeapBytes = GC.GetTotalMemory(false) |}
              memoryBytes = Process.GetCurrentProcess().WorkingSet64 |}
}

[<EntryPoint>]
let main args =
    use parent = Process.GetProcessById(int args[0])
    use guard = new Timer((fun _ ->
        if parent.HasExited then
            if not (OperatingSystem.IsWindows()) then killGroup(-Environment.ProcessId, 9) |> ignore
            Environment.Exit(1)), null, 500, 500)
    printfn "@@CHECKER_READY"
    let mutable running = true
    while running do
        let line = Console.ReadLine()
        if isNull line then running <- false
        else
            try
                use request = JsonDocument.Parse(line)
                let result = compile request.RootElement |> Async.RunSynchronously
                printfn "@@CHECKER_RESULT %s" (JsonSerializer.Serialize(result))
            with error ->
                checkers.Clear()
                order.Clear()
                printfn "@@CHECKER_RESULT %s" (JsonSerializer.Serialize({| ok = false; errors = [error.ToString()] |}))
    0
