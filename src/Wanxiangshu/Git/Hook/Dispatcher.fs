namespace Wanxiangshu.Git.Hook

open System
open Fable.Core
open Fable.Core.JsInterop
open Wanxiangshu.Git
open Wanxiangshu.Host
open Wanxiangshu.Persistence.EventStore

/// durable-convergence-008. Product startup only ENSURES the Git hook membrane
/// and the per-remote fetch refspec baseline.
/// Actual full bidirectional convergence runs later in an independent Git-hook
/// process through resources/git/wanxiang-hook.mjs + HookSync.
[<RequireQualifiedAccess>]
module HookDispatcher =

    [<Literal>]
    let SyncActiveEnv = "WANXIANG_GIT_SYNC_ACTIVE"

    [<Literal>]
    let OwnershipMarker = "wanxiang-hook-dispatcher"

    [<Literal>]
    let IncompleteDiagnosis = "Git integration incomplete"

    type HookKind =
        | ReferenceTransaction
        | PrePush

    type HookInstallVerdict =
        | Installed
        | AlreadyOwned
        | ForeignHook of path: string
        | DiagnoseIncomplete of reason: string

    [<Import("existsSync", "node:fs")>]
    let private existsSync (path: string) : bool = jsNative

    [<Import("readFileSync", "node:fs")>]
    let private readFileSync (path: string) (encoding: string) : string = jsNative

    [<Import("writeFileSync", "node:fs")>]
    let private writeFileSync (path: string) (content: string) (options: obj) : unit = jsNative

    [<Import("chmodSync", "node:fs")>]
    let private chmodSync (path: string) (mode: int) : unit = jsNative

    [<Import("mkdirSync", "node:fs")>]
    let private mkdirSync (path: string) (options: obj) : unit = jsNative

    [<Import("join", "node:path")>]
    let private joinPath (left: string) (right: string) : string = jsNative

    [<Import("dirname", "node:path")>]
    let private dirname (path: string) : string = jsNative

    [<Import("tmpdir", "node:os")>]
    let private tempDirectory () : string = jsNative

    [<Import("fileURLToPath", "node:url")>]
    let private fileURLToPath (url: string) : string = jsNative

    [<Emit("import.meta.url")>]
    let private importMetaUrl: string = jsNative

    [<Emit("process.platform")>]
    let private platform: string = jsNative

    let private hookFileName =
        function
        | HookKind.ReferenceTransaction -> "reference-transaction"
        | HookKind.PrePush -> "pre-push"

    let private hookRunnerArgument =
        function
        | HookKind.ReferenceTransaction -> "reference-transaction"
        | HookKind.PrePush -> "pre-push"

    let private tryReadHook path =
        if existsSync path then
            Some(readFileSync path "utf8")
        else
            None

    let private containsOwnershipMarker (body: string) = body.Contains OwnershipMarker

    let classifyExistingHook (existingBody: string option) : HookInstallVerdict =
        match existingBody with
        | None -> Installed
        | Some body when containsOwnershipMarker body -> AlreadyOwned
        | Some _ -> ForeignHook ""

    let private writeShim path body =
        writeFileSync path body (createObj [ "encoding" ==> "utf8"; "mode" ==> 0o755 ])

        try
            chmodSync path 0o755
        with _ ->
            ()

    let private installAtPath path shimBody =
        match classifyExistingHook (tryReadHook path) with
        | Installed ->
            writeShim path shimBody
            Installed
        | AlreadyOwned ->
            writeShim path shimBody
            AlreadyOwned
        | ForeignHook _ -> ForeignHook path
        | DiagnoseIncomplete reason -> DiagnoseIncomplete reason

    let installOrDiagnose (hooksDir: string) (kind: HookKind) (shimBody: string) : HookInstallVerdict =
        if not (containsOwnershipMarker shimBody) then
            DiagnoseIncomplete(sprintf "%s: shim body missing ownership marker" IncompleteDiagnosis)
        else
            let path = joinPath hooksDir (hookFileName kind)
            installAtPath path shimBody

    let shimHeaderComment =
        sprintf "# %s\n# ownership: Wanxiangshu HookDispatcher" OwnershipMarker

    let private shellQuote (value: string) =
        "'" + value.Replace("'", "'\"'\"'") + "'"

    /// Compiled HookDispatcher lives at dist/Git/Hook. The independently shipped
    /// runner lives at resources/git under the same package root.
    let private runnerPath () =
        let here = dirname (fileURLToPath importMetaUrl)
        let packageRoot = dirname (dirname (dirname here))
        joinPath (joinPath (joinPath packageRoot "resources") "git") "wanxiang-hook.mjs"

    let private remoteTrackingRefPattern () =
        let marker = "__wanxiang_remote__"

        StoreRef.remoteTracking marker
        |> fun template -> template.Replace(marker, "[^/]+")

    let private shimBody kind =
        let runner = shellQuote (runnerPath ())

        let body =
            [ "#!/bin/sh"
              shimHeaderComment
              sprintf "if [ \"${%s:-}\" = \"1\" ]; then exit 0; fi" SyncActiveEnv ]

        let invocation =
            match kind with
            | HookKind.ReferenceTransaction ->
                [ "if [ \"${1:-}\" != \"committed\" ]; then exit 0; fi"
                  "wanxiang_stdin=$(cat)"
                  sprintf
                      "if ! printf '%%s\\n' \"$wanxiang_stdin\" | grep -Eq '^[0-9a-fA-F]+[[:space:]]+[0-9a-fA-F]+[[:space:]]+%s$'; then exit 0; fi"
                      (remoteTrackingRefPattern ())
                  sprintf
                      "printf '%%s\\n' \"$wanxiang_stdin\" | exec /usr/bin/env node %s %s \"$@\""
                      runner
                      (hookRunnerArgument kind) ]
            | HookKind.PrePush -> [ sprintf "exec /usr/bin/env node %s %s \"$@\"" runner (hookRunnerArgument kind) ]

        String.concat "\n" (body @ invocation @ [ "" ])

    let private hooksDirectory workspace =
        GitSubject.execIn workspace [| "rev-parse"; "--path-format=absolute"; "--git-path"; "hooks" |]
        |> fun value -> value.Trim()

    let private remotes workspace =
        let output = GitSubject.execIn workspace [| "remote" |]

        output.Split('\n')
        |> Array.map (fun value -> value.Trim())
        |> Array.filter (String.IsNullOrWhiteSpace >> not)
        |> Array.toList

    let private remoteFetchSpecs workspace remote =
        try
            let output =
                GitSubject.execIn workspace [| "config"; "--get-all"; sprintf "remote.%s.fetch" remote |]

            output.Split('\n')
            |> Array.map (fun value -> value.Trim())
            |> Array.filter (String.IsNullOrWhiteSpace >> not)
            |> Array.toList
        with _ ->
            []

    let private ensureRemoteFetchRefspec workspace remote expected =
        if remoteFetchSpecs workspace remote |> List.contains expected then
            ()
        else
            GitSubject.execIn workspace [| "config"; "--add"; sprintf "remote.%s.fetch" remote; expected |]
            |> ignore

    let private ensureRemoteStoreFetchRefspec workspace remote =
        let expected = sprintf "+%s:%s" StoreRef.canonical (StoreRef.remoteTracking remote)
        ensureRemoteFetchRefspec workspace remote expected

    let private ensureRemoteHeadsFetchRefspec workspace remote =
        let expected = sprintf "+refs/heads/*:refs/remotes/%s/*" remote
        ensureRemoteFetchRefspec workspace remote expected

    let private ensureRemoteRefs workspace =
        for remote in remotes workspace do
            ensureRemoteStoreFetchRefspec workspace remote
            ensureRemoteHeadsFetchRefspec workspace remote

    let private tryGitConfig workspace key =
        try
            let value =
                GitSubject.execIn workspace [| "config"; "--get"; key |]
                |> fun output -> output.Trim()

            if String.IsNullOrWhiteSpace value then None else Some value
        with _ ->
            None

    let private gitCommonDir workspace =
        GitSubject.execIn workspace [| "rev-parse"; "--path-format=absolute"; "--git-common-dir" |]
        |> fun value -> value.Trim()

    let private hasUserSshMultiplex (command: string) =
        let lower = command.ToLowerInvariant()
        lower.Contains("controlmaster=") || lower.Contains("controlpath=")

    let private multiplexSuffix controlPath =
        String.concat
            " "
            [ "-o ControlMaster=auto"
              "-o ControlPersist=15s"
              "-o " + shellQuote ("ControlPath=" + controlPath) ]

    let private legacyManagedControlPath commonDir =
        joinPath (joinPath commonDir "wanxiang") "ssh-%C"

    let private legacyManagedSshWrapperPath commonDir =
        joinPath (joinPath commonDir "wanxiang") "ssh-command"

    let private legacyManagedSshWrapperCommand commonDir =
        shellQuote (legacyManagedSshWrapperPath commonDir)

    let private repoSshKey commonDir =
        HostDigest.sha256Hex commonDir |> fun digest -> digest.Substring(0, 12)

    let private managedSocketDirectory commonDir =
        joinPath (tempDirectory ()) ("wanxiang-ssh-" + repoSshKey commonDir)

    let private managedControlPath commonDir =
        joinPath (managedSocketDirectory commonDir) "ssh-%C"

    let private managedSshWrapperPath commonDir =
        joinPath (joinPath commonDir "wanxiangshu") "ssh-command"

    let private managedSshWrapperCommand commonDir =
        shellQuote (managedSshWrapperPath commonDir)

    let private isLegacyWrapperCommand commonDir (command: string) =
        command = legacyManagedSshWrapperCommand commonDir
        || command = legacyManagedSshWrapperPath commonDir

    let private normalizeBaseCommand commonDir baseCommand =
        if isLegacyWrapperCommand commonDir baseCommand then
            "ssh"
        else
            baseCommand

    let private stripSuffixToBase commonDir (command: string) controlPath =
        let suffix = " " + multiplexSuffix controlPath
        let hit = command.EndsWith(suffix, StringComparison.Ordinal)

        let baseCommand =
            if hit then
                command.Substring(0, command.Length - suffix.Length)
            else
                ""

        (hit, baseCommand)

    let private pickSuffixBase commonDir (command: string) (hit: bool, baseCommand: string) =
        match hit with
        | false -> None
        | true -> Some(normalizeBaseCommand commonDir baseCommand)

    let private tryStripManagedCommand commonDir (command: string) =
        match isLegacyWrapperCommand commonDir command with
        | true -> Some "ssh"
        | false ->
            [ legacyManagedControlPath commonDir; managedControlPath commonDir ]
            |> List.map (stripSuffixToBase commonDir command)
            |> List.tryPick (pickSuffixBase commonDir command)

    let private managedSshWrapperBody commonDir baseCommand =
        let socketDirectory = managedSocketDirectory commonDir
        let controlPath = managedControlPath commonDir

        String.concat
            "\n"
            [ "#!/bin/sh"
              sprintf "# %s ssh-command" OwnershipMarker
              "set -eu"
              "umask 077"
              "mkdir -p " + shellQuote socketDirectory
              "chmod 700 " + shellQuote socketDirectory
              baseCommand + " " + multiplexSuffix controlPath + " \"$@\""
              "" ]

    let private installManagedSshWrapper commonDir baseCommand =
        let wrapperPath = managedSshWrapperPath commonDir
        mkdirSync (dirname wrapperPath) (createObj [ "recursive" ==> true; "mode" ==> 0o700 ])

        writeFileSync
            wrapperPath
            (managedSshWrapperBody commonDir baseCommand)
            (createObj [ "encoding" ==> "utf8"; "mode" ==> 0o700 ])

        chmodSync wrapperPath 0o700

    let private ownedSshWrapperExists commonDir =
        let wrapperPath = managedSshWrapperPath commonDir

        existsSync wrapperPath
        && containsOwnershipMarker (readFileSync wrapperPath "utf8")

    let private ensureOwnedSshWrapperStillPresent commonDir =
        if not (ownedSshWrapperExists commonDir) then
            failwithf
                "%s: managed SSH wrapper is missing or not owned: %s"
                IncompleteDiagnosis
                (managedSshWrapperPath commonDir)

    let private installManagedSshCommand workspace commonDir baseCommand =
        installManagedSshWrapper commonDir baseCommand

        GitSubject.execIn workspace [| "config"; "--local"; "core.sshCommand"; managedSshWrapperCommand commonDir |]
        |> ignore

    let private oldManagedCommandBase commonDir current =
        match tryStripManagedCommand commonDir current with
        | Some baseCommand -> Some baseCommand
        | None when hasUserSshMultiplex current -> None
        | None -> Some current

    let private wrapperBodyReferencesLegacyRoot commonDir =
        try
            let body = readFileSync (managedSshWrapperPath commonDir) "utf8"

            body.Contains("/wanxiang/ssh-")
            || body.Contains(legacyManagedSshWrapperPath commonDir)
        with _ ->
            false

    let private rewriteStaleOwnedWrapper workspace commonDir =
        installManagedSshCommand workspace commonDir "ssh"

    let private refreshOwnedWrapper workspace commonDir =
        match wrapperBodyReferencesLegacyRoot commonDir with
        | true -> rewriteStaleOwnedWrapper workspace commonDir
        | false -> ensureOwnedSshWrapperStillPresent commonDir

    let private refreshForeignCommand workspace commonDir current =
        oldManagedCommandBase commonDir current
        |> Option.iter (installManagedSshCommand workspace commonDir)

    let private ensureManagedSshCommand workspace commonDir current =
        match current = managedSshWrapperCommand commonDir with
        | true -> refreshOwnedWrapper workspace commonDir
        | false -> refreshForeignCommand workspace commonDir current

    let private ensureUnixSshMultiplex workspace =
        let commonDir = gitCommonDir workspace
        let current = tryGitConfig workspace "core.sshCommand" |> Option.defaultValue "ssh"
        ensureManagedSshCommand workspace commonDir current

    let private ensureSshMultiplex workspace =
        if platform = "win32" then
            ()
        else
            ensureUnixSshMultiplex workspace

    let private ensureWorkspace workspace : Result<unit, string> =
        let hooksDir = hooksDirectory workspace

        let verdicts =
            [ HookKind.ReferenceTransaction; HookKind.PrePush ]
            |> List.map (fun kind -> kind, installOrDiagnose hooksDir kind (shimBody kind))

        match
            verdicts
            |> List.tryPick (fun (_, verdict) ->
                match verdict with
                | ForeignHook path -> Some(sprintf "%s: foreign hook at %s" IncompleteDiagnosis path)
                | DiagnoseIncomplete reason -> Some reason
                | Installed
                | AlreadyOwned -> None)
        with
        | Some error -> Error error
        | None ->
            ensureRemoteRefs workspace
            ensureSshMultiplex workspace
            Ok()

    /// Startup-only ensure. There is intentionally no fetch/pull/push here.
    /// Both installed hooks later launch the same standalone FULL converge path.
    let ensure (workspace: string) : Result<unit, string> =
        try
            ensureWorkspace workspace
        with ex ->
            Error(sprintf "%s: %s" IncompleteDiagnosis ex.Message)
