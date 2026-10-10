namespace Wanxiangshu.OpenCode

open System
open System.Text
open System.Threading
open System.Threading.Tasks
open ToolHostCodec
open Wanxiangshu.Execution.Delegation.Fork
open Wanxiangshu.Execution.Session.Recovery.SessionRecovery
open Wanxiangshu.Foundation
open Wanxiangshu.Foundation.Identity
open Wanxiangshu.Participant.Provider
open Wanxiangshu.Process

/// Bounded command execution. Provider verb: `run` (DevOps).
module ExecutorTool =

    [<RequireQualifiedAccess>]
    module Path =
        [<RequireQualifiedAccess>]
        module Run =
            [<Literal>]
            let Description = "tool/run/description"

            [<Literal>]
            let ArgCommand = "tool/run/arg-command"

            [<Literal>]
            let ArgDeadlineSeconds = "tool/run/arg-deadline_seconds"

            [<Literal>]
            let ArgOutputBudgetBytes = "tool/run/arg-output_budget_bytes"

            [<Literal>]
            let ArgWorldLock = "tool/run/arg-world_lock"

            [<Literal>]
            let MissingCommand = "tool/run/missing-command"

            [<Literal>]
            let FinitePositive = "tool/run/finite-positive"

            [<Literal>]
            let FiniteNonNegativeInteger = "tool/run/finite-non-negative-integer"

            [<Literal>]
            let MustBeInteger = "tool/run/must-be-integer"

            [<Literal>]
            let Timeout = "tool/run/timeout"

            [<Literal>]
            let SpawnFailed = "tool/run/spawn-failed"

            [<Literal>]
            let Cancelled = "tool/run/cancelled"

            [<Literal>]
            let ExecutionFailed = "tool/run/execution-failed"

            [<Literal>]
            let CannotRunFromContext = "tool/run/cannot-run-from-context"

            [<Literal>]
            let CannotReadOutputUntilAuthority = "tool/run/cannot-read-output-until-authority"

            [<Literal>]
            let OutputTruncated = "tool/run/output-truncated"

            [<Literal>]
            let LargeOutputRecoveryBlocked = "tool/run/large-output-recovery-blocked"

    /// Provider-visible execution verb. Distillation is invoked inside this
    /// tool and is never a separate provider verb (process-execution-011 / DISTILL-010).
    [<Literal>]
    let RunToolName = "run"

    type Request =
        { Command: string
          DeadlineSeconds: float
          OutputBudgetBytes: int64
          WorldLock: bool }

    let private lang (ctx: HostToolContext) =
        ProviderLanguageBinding.forSessionText ctx.SessionId

    let private prose language path =
        ProviderProse.render language path Map.empty

    let private namedProse language path name =
        ProviderProse.render language path (Map [ "name", name ])

    let private finitePositive language (name: string) (value: float) =
        if Double.IsNaN value || Double.IsInfinity value || value <= 0.0 then
            Error(namedProse language Path.Run.FinitePositive name)
        else
            Ok value

    let private finiteOutput language (name: string) (value: float) =
        if
            Double.IsNaN value
            || Double.IsInfinity value
            || value < 0.0
            || value > float Int64.MaxValue
        then
            Error(namedProse language Path.Run.FiniteNonNegativeInteger name)
        elif value <> Math.Floor value then
            Error(namedProse language Path.Run.MustBeInteger name)
        else
            Ok(int64 value)

    let private decodeRun (language: ProviderLanguage) (args: HostToolArguments) =
        let command = args.Text "command"
        let deadline = args.OptionalNumber "deadline_seconds" |> Option.defaultValue 30.0

        let budget =
            args.OptionalNumber "output_budget_bytes" |> Option.defaultValue 65536.0

        let worldLock =
            match args.OptionalBool "world_lock" with
            | Some value -> Ok value
            | None -> Ok false

        if String.IsNullOrWhiteSpace command then
            Error(prose language Path.Run.MissingCommand)
        else
            match
                finitePositive language "deadline_seconds" deadline,
                finiteOutput language "output_budget_bytes" budget,
                worldLock
            with
            | Ok deadlineSeconds, Ok outputBytes, Ok lock ->
                Ok
                    { Command = command
                      DeadlineSeconds = deadlineSeconds
                      OutputBudgetBytes = outputBytes
                      WorldLock = lock }
            | Error error, _, _
            | _, Error error, _
            | _, _, Error error -> Error error

    let private consequence (message: string) =
        tomlObjectWithInstructions [ message ] []

    let private processConsequence language (processError: ProcessError) =
        match processError with
        | ProcessError.TimeoutExceeded _ -> consequence (prose language Path.Run.Timeout)
        | ProcessError.SpawnFailed _ -> consequence (prose language Path.Run.SpawnFailed)
        | ProcessError.ProcessCancelled _ -> consequence (prose language Path.Run.Cancelled)
        | ProcessError.ExecutionFailed _ -> consequence (prose language Path.Run.ExecutionFailed)

    let private directoryFor (scope: ToolRuntimeScope) (context: HostToolContext) =
        if String.IsNullOrWhiteSpace context.SessionId then
            scope.WorkspaceDirectory
        else
            scope.DirectoryFor context.SessionId |> Option.orElse scope.WorkspaceDirectory

    let private estimatedMemory (worldLock: bool) =
        if worldLock then
            EstimatedMemory.Large
        else
            EstimatedMemory.Medium

    let private completedToml (exitCode: int) (stdout: string) (stderr: string) =
        let fields =
            [ "exit_code", TInt exitCode
              "stdout", TString(if isNull stdout then "" else stdout)
              "stderr", TString(if isNull stderr then "" else stderr) ]

        tomlObject fields

    let internal formatSpooledOutcome (exitCode: int) (output: string) =
        tomlObject [ "exit_code", TInt exitCode; "output", TString output ]

    let private formatTruncatedOutcome language limitBytes exitCode output =
        let notice =
            ProviderProse.render language Path.Run.OutputTruncated (Map [ "budget_bytes", string limitBytes ])

        tomlObject
            [ "exit_code", TInt exitCode
              "output", TString output
              "output_truncated", TBool true
              "output_notice", TString notice ]

    let private formatTailOutcome language limitBytes exitCode truncated output =
        match truncated with
        | true -> formatTruncatedOutcome language limitBytes exitCode output
        | false -> formatSpooledOutcome exitCode output

    let private readSpooledTail (language: ProviderLanguage) (budgetBytes: int64) (exitCode: int) (spoolPath: string) =
        task {
            let limitBytes = int (min (int64 Int32.MaxValue) budgetBytes)
            let! tail = Spool.readLatestTail limitBytes spoolPath
            let rawTail = Encoding.UTF8.GetString tail.Bytes
            return formatTailOutcome language limitBytes exitCode tail.Truncated rawTail
        }

    let private finalizeSpooledWithAuthority
        (scope: ToolRuntimeScope)
        (language: ProviderLanguage)
        (root: SessionId)
        (budgetBytes: int64)
        (exitCode: int)
        (spoolPath: string)
        =
        task {
            let! recovery = scope.RequireCurrentProcessJoin root

            match recovery with
            | FamilyRecovery.FamilyBlocked _ -> return consequence (prose language Path.Run.LargeOutputRecoveryBlocked)
            | FamilyRecovery.FamilyWaiting _
            | FamilyRecovery.FamilyReady _ -> return! readSpooledTail language budgetBytes exitCode spoolPath
        }

    let private finalizeSpooledBody
        (scope: ToolRuntimeScope)
        (language: ProviderLanguage)
        (context: HostToolContext)
        (budgetBytes: int64)
        (exitCode: int)
        (spoolPath: string)
        =
        task {
            if String.IsNullOrWhiteSpace context.SessionId then
                return consequence (prose language Path.Run.CannotReadOutputUntilAuthority)
            else
                return!
                    finalizeSpooledWithAuthority
                        scope
                        language
                        (SessionId.create context.SessionId)
                        budgetBytes
                        exitCode
                        spoolPath
        }

    let private finalizeSpooled
        (scope: ToolRuntimeScope)
        (language: ProviderLanguage)
        (context: HostToolContext)
        (budgetBytes: int64)
        (exitCode: int)
        (spoolPath: string)
        =
        task {
            try
                return! finalizeSpooledBody scope language context budgetBytes exitCode spoolPath
            finally
                Spool.delete spoolPath
        }

    let private truncateCompleted
        (language: ProviderLanguage)
        (budgetBytes: int64)
        (exitCode: int)
        (fullBytes: byte array)
        =
        let limitBytes = int (min (int64 Int32.MaxValue) budgetBytes)
        let tail = Spool.retainLatestBytes limitBytes [||] fullBytes |> Spool.alignUtf8Tail
        let rawTail = Encoding.UTF8.GetString tail
        formatTruncatedOutcome language limitBytes exitCode rawTail

    let private completedOutcome
        (language: ProviderLanguage)
        (budgetBytes: int64)
        (exitCode: int)
        (stdout: string)
        (stderr: string)
        =
        let fullBytes =
            Encoding.UTF8.GetBytes(stdout + (if String.IsNullOrEmpty stderr then "" else "\n" + stderr))

        if int64 fullBytes.Length > budgetBytes then
            truncateCompleted language budgetBytes exitCode fullBytes
        else
            completedToml exitCode stdout stderr

    let private interpretOutcome
        (scope: ToolRuntimeScope)
        (request: Request)
        (language: ProviderLanguage)
        (context: HostToolContext)
        (result: Result<ProcessOutcome, ProcessError>)
        =
        match result with
        | Error processError -> task { return processConsequence language processError }
        | Ok(ProcessOutcome.Completed(exitCode, stdout, stderr, _)) ->
            task { return completedOutcome language request.OutputBudgetBytes exitCode stdout stderr }
        | Ok(ProcessOutcome.Spooled(exitCode, spoolPath, _totalBytes, _chunkCount)) ->
            finalizeSpooled scope language context request.OutputBudgetBytes exitCode spoolPath

    let private runPrepared
        (scope: ToolRuntimeScope)
        (request: Request)
        (context: HostToolContext)
        (language: ProviderLanguage)
        =
        task {
            let directory = directoryFor scope context

            let estimate =
                { EstimatedRuntime = RuntimeSeconds request.DeadlineSeconds
                  EstimatedOutput = OutputBytes request.OutputBudgetBytes
                  EstimatedMemory = estimatedMemory request.WorldLock }

            let command =
                { FileName = "sh"
                  Arguments = [ "-lc"; request.Command ]
                  WorkingDirectory = directory
                  Environment = None
                  Stdin = None
                  PtyOptions = None }

            use cancellation = new CancellationTokenSource()
            let detachAbort = context.AttachAbort cancellation.Cancel

            let processContext: ProcessContext =
                { WorkingDirectory = directory
                  HardLimit = scope.ProcessHardLimit }

            let! result =
                try
                    ProcessRunner.run command estimate processContext cancellation.Token
                finally
                    detachAbort ()

            return! interpretOutcome scope request language context result
        }

    let private execute (scope: ToolRuntimeScope) (request: Request) (context: HostToolContext) =
        task {
            let language = lang context

            match scope.RuntimeFor context with
            | Error _ -> return consequence (prose language Path.Run.CannotRunFromContext)
            | Ok _ -> return! runPrepared scope request context language
        }

    let runAdmission: ToolAdmission =
        ToolAdmission.OfficeRole(fun _ r -> r = Role.DevOps)

    let runSpec (factory: HostToolFactory) (scope: ToolRuntimeScope) : ToolSpec =
        let language = ProviderLanguageBinding.readGlobalPreference ()

        { Name = "run"
          Description = prose language Path.Run.Description
          Arguments =
            [ "command", ToolHostCodec.stringSchemaDescribed (prose language Path.Run.ArgCommand) factory
              "deadline_seconds",
              ToolHostCodec.numberSchemaDescribed (prose language Path.Run.ArgDeadlineSeconds) factory
              "output_budget_bytes",
              ToolHostCodec.numberSchemaDescribed (prose language Path.Run.ArgOutputBudgetBytes) factory
              "world_lock", ToolHostCodec.boolSchemaDescribed (prose language Path.Run.ArgWorldLock) factory ]
          Admission = runAdmission
          Execute =
            fun args context ->
                match decodeRun (lang context) args with
                | Ok request -> execute scope request context
                | Error decodeError -> task { return consequence decodeError } }
