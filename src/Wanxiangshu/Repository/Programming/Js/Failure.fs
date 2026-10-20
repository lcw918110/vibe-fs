namespace Wanxiangshu.Repository.Programming.Js

open Wanxiangshu.Foundation.Identity
open Wanxiangshu.Persistence.EventStore

type JsTransactionId = private JsTransactionId of string

module JsTransactionId =
    let create (value: string) = JsTransactionId value
    let value (JsTransactionId v) = v

    let generate () =
        JsTransactionId(System.Guid.NewGuid().ToString("N"))

[<RequireQualifiedAccess>]
type JsTransactionAppendPhase =
    | Prepared
    | Committed

type JsTransactionAppendFailure =
    { Phase: JsTransactionAppendPhase
      TransactionId: JsTransactionId
      EventId: EventId
      Error: AppendError }

/// DSL-class: Vocabulary — JS-019: stable failure codes (proposal §77/§77.1) —
/// program-foreseeable failures are typed branches with stable LLM-visible
/// codes; exceptions are only for crashes. Codes are frozen once shipped.
[<RequireQualifiedAccess>]
type JsFailure =
    | InvalidProgram
    | ProgramFailed of string
    | ProgramTimeout
    | ProgramResourceLimit
    | PermissionDenied of string
    | PathDenied of string
    | FileNotFound of string
    | FileAlreadyExists of string
    | FileReadFailed of string
    | RgFailed of string
    | DirectoryNotEmpty of string
    | DirectoryExists of string
    | ParentDirectoryMissing of string
    | CrossDeviceMove of string
    | InvalidArgument of string
    | InvalidUtf8 of string
    | AnchorEmptyContent
    | AnchorInvalidPattern
    | AnchorNotFound of string
    | AnchorNotUnique
    | AnchorCrossFile
    | InvalidEdit of string
    | EditNotFound of string
    | EditAmbiguous of string
    | EditOverlap of string
    | DuplicateMutationTarget of string
    | ResultTooLarge of string option
    | InvalidReturnValue
    | ReadOnlyMutationRejected
    | FileChanged of string
    | TransactionPrepareFailed
    | TransactionCommitFailed
    | TransactionRollbackFailed
    | TransactionRecoveryRequired
    | TransactionPersistenceFailed of JsTransactionAppendFailure
    | UnknownMember
    | PlanReviewRequired
    | ConcurrencyLimit
    | HandleUnknown of string
    | CannotParkWhileAbandoning
    | Abandoned
    | CallableSpecIncomplete of string
    | CallCommandUnsupported of string
    | CallWaitTimeout
    | CallChildUnknown of string
    | CallParkUnsupported
    | CallHostUnavailable
    | AskRefused of string
    | DelegationUnavailable
    | ViewDeclarationInvalid of string

module JsFailure =

    let private transactionPhaseCode (phase: JsTransactionAppendPhase) =
        match phase with
        | JsTransactionAppendPhase.Prepared -> "TRANSACTION_PREPARE_"
        | JsTransactionAppendPhase.Committed -> "TRANSACTION_COMMIT_"

    let private appendFailureCode (error: AppendError) =
        match error with
        | AppendError.AppendNotAttempted _ -> "NOT_ATTEMPTED"
        | AppendError.CommitUnknown _ -> "UNKNOWN"
        | AppendError.NoNewWriteReleaseFailed _ -> "NO_NEW_WRITE_RELEASE_FAILED"
        | AppendError.StorageInvalid _
        | AppendError.SemanticCut _
        | AppendError.AppendFailed _ -> "FAILED"

    /// Stable machine-readable code; the LLM-visible rendering is code + reason.
    let code (failure: JsFailure) : string =
        match failure with
        | JsFailure.InvalidProgram -> "INVALID_PROGRAM"
        | JsFailure.ProgramFailed _ -> "PROGRAM_FAILED"
        | JsFailure.ProgramTimeout -> "PROGRAM_TIMEOUT"
        | JsFailure.ProgramResourceLimit -> "PROGRAM_RESOURCE_LIMIT"
        | JsFailure.PermissionDenied _ -> "PERMISSION_DENIED"
        | JsFailure.PathDenied _ -> "PATH_DENIED"
        | JsFailure.FileNotFound _ -> "FILE_NOT_FOUND"
        | JsFailure.FileAlreadyExists _ -> "FILE_ALREADY_EXISTS"
        | JsFailure.FileReadFailed _ -> "FILE_READ_FAILED"
        | JsFailure.RgFailed _ -> "RG_FAILED"
        | JsFailure.DirectoryNotEmpty _ -> "DIRECTORY_NOT_EMPTY"
        | JsFailure.DirectoryExists _ -> "DIRECTORY_EXISTS"
        | JsFailure.ParentDirectoryMissing _ -> "PARENT_DIRECTORY_MISSING"
        | JsFailure.CrossDeviceMove _ -> "CROSS_DEVICE_MOVE"
        | JsFailure.InvalidArgument _ -> "INVALID_ARGUMENT"
        | JsFailure.InvalidUtf8 _ -> "INVALID_UTF8"
        | JsFailure.AnchorEmptyContent -> "EMPTY_ANCHOR_CONTENT"
        | JsFailure.AnchorInvalidPattern -> "INVALID_ANCHOR_PATTERN"
        | JsFailure.AnchorNotFound _ -> "ANCHOR_NOT_FOUND"
        | JsFailure.AnchorNotUnique -> "ANCHOR_NOT_UNIQUE"
        | JsFailure.AnchorCrossFile -> "ANCHOR_CROSS_FILE"
        | JsFailure.InvalidEdit _ -> "INVALID_EDIT"
        | JsFailure.EditNotFound _ -> "EDIT_NOT_FOUND"
        | JsFailure.EditAmbiguous _ -> "EDIT_AMBIGUOUS"
        | JsFailure.EditOverlap _ -> "EDIT_OVERLAP"
        | JsFailure.DuplicateMutationTarget _ -> "DUPLICATE_MUTATION_TARGET"
        | JsFailure.ResultTooLarge _ -> "RESULT_TOO_LARGE"
        | JsFailure.InvalidReturnValue -> "INVALID_RETURN_VALUE"
        | JsFailure.ReadOnlyMutationRejected -> "READ_ONLY_MUTATION_REJECTED"
        | JsFailure.FileChanged _ -> "FILE_CHANGED"
        | JsFailure.TransactionPrepareFailed -> "TRANSACTION_PREPARE_FAILED"
        | JsFailure.TransactionCommitFailed -> "TRANSACTION_COMMIT_FAILED"
        | JsFailure.TransactionRollbackFailed -> "TRANSACTION_ROLLBACK_FAILED"
        | JsFailure.TransactionRecoveryRequired -> "TRANSACTION_RECOVERY_REQUIRED"
        | JsFailure.TransactionPersistenceFailed failure ->
            transactionPhaseCode failure.Phase + appendFailureCode failure.Error
        | JsFailure.UnknownMember -> "UNKNOWN_MEMBER"
        | JsFailure.PlanReviewRequired -> "PLAN_REVIEW_REQUIRED"
        | JsFailure.ConcurrencyLimit -> "CONCURRENCY_LIMIT"
        | JsFailure.HandleUnknown _ -> "HANDLE_UNKNOWN"
        | JsFailure.CannotParkWhileAbandoning -> "CANNOT_PARK_WHILE_ABANDONING"
        | JsFailure.Abandoned -> "ABANDONED"
        | JsFailure.CallableSpecIncomplete _ -> "CALLABLE_SPEC_INCOMPLETE"
        | JsFailure.CallCommandUnsupported _ -> "CALL_COMMAND_UNSUPPORTED"
        | JsFailure.CallWaitTimeout -> "CALL_WAIT_TIMEOUT"
        | JsFailure.CallChildUnknown _ -> "CALL_CHILD_UNKNOWN"
        | JsFailure.CallParkUnsupported -> "CALL_PARK_UNSUPPORTED"
        | JsFailure.CallHostUnavailable -> "CALL_HOST_UNAVAILABLE"
        | JsFailure.AskRefused _ -> "ASK_REFUSED"
        | JsFailure.DelegationUnavailable -> "DELEGATION_UNAVAILABLE"
        | JsFailure.ViewDeclarationInvalid _ -> "VIEW_DECLARATION_INVALID"

    /// LLM-visible stable reason text (proposal §78: readable, stable, no stack noise).
    let private reasonWithMessage fallback prefix message =
        if System.String.IsNullOrEmpty message then
            fallback
        else
            prefix + message

    let reason (failure: JsFailure) : string =
        match failure with
        | JsFailure.InvalidProgram -> "program source is invalid JavaScript"
        | JsFailure.ProgramFailed message -> reasonWithMessage "program threw" "program threw: " message
        | JsFailure.ProgramTimeout -> "program exceeded its deadline"
        | JsFailure.ProgramResourceLimit -> "program exceeded a resource bound"
        | JsFailure.PermissionDenied capability -> "capability not present in this attempt: " + capability
        | JsFailure.PathDenied path -> "path outside capability boundary: " + path
        | JsFailure.FileNotFound path -> "target file does not exist: " + path
        | JsFailure.FileAlreadyExists path -> "target file already exists: " + path
        | JsFailure.FileReadFailed path -> "file read failed: " + path
        | JsFailure.RgFailed detail -> reasonWithMessage "rg failed" "rg failed: " detail
        | JsFailure.DirectoryNotEmpty path -> "directory is not empty: " + path
        | JsFailure.DirectoryExists path -> "directory already exists: " + path
        | JsFailure.ParentDirectoryMissing path -> "parent directory does not exist: " + path
        | JsFailure.CrossDeviceMove path -> "cross-device move is refused and rolled back: " + path
        | JsFailure.InvalidArgument detail -> reasonWithMessage "invalid argument" "invalid argument: " detail
        | JsFailure.InvalidUtf8 path -> "file is not strict UTF-8: " + path
        | JsFailure.AnchorEmptyContent -> "anchor content is empty"
        | JsFailure.AnchorInvalidPattern -> "anchor RegExp is invalid"
        | JsFailure.AnchorNotFound detail -> reasonWithMessage "anchor did not match" "" detail
        | JsFailure.AnchorNotUnique -> "anchor matches multiple locations and no occurrence was declared"
        | JsFailure.AnchorCrossFile -> "anchor declaration crosses files"
        | JsFailure.InvalidEdit detail -> reasonWithMessage "edit declaration is invalid" "" detail
        | JsFailure.EditNotFound detail -> reasonWithMessage "edit find matched no locations" "" detail
        | JsFailure.EditAmbiguous detail -> reasonWithMessage "edit find matched multiple locations" "" detail
        | JsFailure.EditOverlap detail -> reasonWithMessage "edit changes overlap" "" detail
        | JsFailure.DuplicateMutationTarget path -> "the same path was mutated twice in one program: " + path
        | JsFailure.ResultTooLarge _ -> "result exceeds the output bound"
        | JsFailure.InvalidReturnValue -> "run() return value is not JSON-compatible"
        | JsFailure.ReadOnlyMutationRejected -> "mutations are rejected on read-only surface"
        | JsFailure.FileChanged path -> "target changed since the read snapshot; no implicit retry: " + path
        | JsFailure.TransactionPrepareFailed -> "transaction prepare failed"
        | JsFailure.TransactionCommitFailed -> "transaction commit failed"
        | JsFailure.TransactionRollbackFailed -> "transaction rollback failed"
        | JsFailure.TransactionRecoveryRequired -> "durable transaction recovery is required"
        | JsFailure.TransactionPersistenceFailed failure ->
            sprintf
                "transaction %s event %s: %s"
                (JsTransactionId.value failure.TransactionId)
                (EventId.value failure.EventId)
                (AppendError.describe failure.Error)
        | JsFailure.UnknownMember -> "member is not part of this generated surface"
        | JsFailure.PlanReviewRequired ->
            "Review is required before modifying the plan artifact. You must call review first."
        | JsFailure.ConcurrencyLimit -> "parked-site concurrency limit reached; no queue is formed"
        | JsFailure.HandleUnknown handle -> "no parked site with this handle: " + handle
        | JsFailure.CannotParkWhileAbandoning -> "park is refused while the site is in its abandon cleanup window"
        | JsFailure.Abandoned -> "the parked program was abandoned; its finalizer has run"
        | JsFailure.CallableSpecIncomplete detail -> reasonWithMessage "Runnable spec is incomplete" "" detail
        | JsFailure.CallCommandUnsupported detail ->
            reasonWithMessage "next() returned an unsupported command" "" detail
        | JsFailure.CallWaitTimeout -> "waitFor condition was not met before its timeout"
        | JsFailure.CallChildUnknown name -> "no background process with this name: " + name
        | JsFailure.CallParkUnsupported -> "park verdict requires the orchestrated execution path"
        | JsFailure.CallHostUnavailable -> "js.call has no host loop in this execution path"
        | JsFailure.AskRefused detail -> reasonWithMessage "ask was refused" "" detail
        | JsFailure.DelegationUnavailable -> "fork/join is not wired in this execution path"
        | JsFailure.ViewDeclarationInvalid detail ->
            reasonWithMessage "view declaration is invalid" "view declaration is invalid: " detail

    /// JS-078.1 stable result shape for failures: { ok: false, code, reason }.
    let render (failure: JsFailure) : string =
        "{ ok: false, code: \""
        + code failure
        + "\", reason: \""
        + reason failure
        + "\" }"

    /// Map a structured sandbox sentinel `{ code, reason }` to a typed failure.
    /// Classification uses the code field, never exception-message sniffing (JS-019).
    let ofWire (code: string) (reason: string) : JsFailure =
        let text = if System.String.IsNullOrEmpty reason then code else reason

        let after (prefix: string) =
            if text.StartsWith prefix then
                text.Substring(prefix.Length)
            else
                text

        match code with
        | "INVALID_PROGRAM" -> JsFailure.InvalidProgram
        | "PROGRAM_FAILED" -> JsFailure.ProgramFailed text
        | "PROGRAM_TIMEOUT" -> JsFailure.ProgramTimeout
        | "PROGRAM_RESOURCE_LIMIT" -> JsFailure.ProgramResourceLimit
        | "PERMISSION_DENIED" -> JsFailure.PermissionDenied(after "capability not present in this attempt: ")
        | "PATH_DENIED" -> JsFailure.PathDenied(after "path outside capability boundary: ")
        | "FILE_NOT_FOUND" -> JsFailure.FileNotFound(after "target file does not exist: ")
        | "FILE_ALREADY_EXISTS" -> JsFailure.FileAlreadyExists(after "target file already exists: ")
        | "FILE_READ_FAILED" -> JsFailure.FileReadFailed(after "file read failed: ")
        | "RG_FAILED" -> JsFailure.RgFailed(after "rg failed: ")
        | "DIRECTORY_NOT_EMPTY" -> JsFailure.DirectoryNotEmpty(after "directory is not empty: ")
        | "DIRECTORY_EXISTS" -> JsFailure.DirectoryExists(after "directory already exists: ")
        | "PARENT_DIRECTORY_MISSING" -> JsFailure.ParentDirectoryMissing(after "parent directory does not exist: ")
        | "CROSS_DEVICE_MOVE" -> JsFailure.CrossDeviceMove(after "cross-device move is refused and rolled back: ")
        | "INVALID_ARGUMENT" -> JsFailure.InvalidArgument(after "invalid argument: ")
        | "INVALID_UTF8" -> JsFailure.InvalidUtf8(after "file is not strict UTF-8: ")
        | "EMPTY_ANCHOR_CONTENT" -> JsFailure.AnchorEmptyContent
        | "INVALID_ANCHOR_PATTERN" -> JsFailure.AnchorInvalidPattern
        | "ANCHOR_NOT_FOUND" -> JsFailure.AnchorNotFound text
        | "ANCHOR_NOT_UNIQUE" -> JsFailure.AnchorNotUnique
        | "ANCHOR_CROSS_FILE" -> JsFailure.AnchorCrossFile
        | "INVALID_EDIT" -> JsFailure.InvalidEdit text
        | "EDIT_NOT_FOUND" -> JsFailure.EditNotFound text
        | "EDIT_AMBIGUOUS" -> JsFailure.EditAmbiguous text
        | "EDIT_OVERLAP" -> JsFailure.EditOverlap text
        | "DUPLICATE_MUTATION_TARGET" ->
            JsFailure.DuplicateMutationTarget(after "the same path was mutated twice in one program: ")
        | "RESULT_TOO_LARGE" -> JsFailure.ResultTooLarge None
        | "INVALID_RETURN_VALUE" -> JsFailure.InvalidReturnValue
        | "READ_ONLY_MUTATION_REJECTED" -> JsFailure.ReadOnlyMutationRejected
        | "FILE_CHANGED" -> JsFailure.FileChanged(after "target changed since the read snapshot; no implicit retry: ")
        | "TRANSACTION_PREPARE_FAILED" -> JsFailure.TransactionPrepareFailed
        | "TRANSACTION_COMMIT_FAILED" -> JsFailure.TransactionCommitFailed
        | "TRANSACTION_ROLLBACK_FAILED" -> JsFailure.TransactionRollbackFailed
        | "TRANSACTION_RECOVERY_REQUIRED" -> JsFailure.TransactionRecoveryRequired
        | "UNKNOWN_MEMBER" -> JsFailure.UnknownMember
        | "PLAN_REVIEW_REQUIRED" -> JsFailure.PlanReviewRequired
        | "CONCURRENCY_LIMIT" -> JsFailure.ConcurrencyLimit
        | "HANDLE_UNKNOWN" -> JsFailure.HandleUnknown(after "no parked site with this handle: ")
        | "CANNOT_PARK_WHILE_ABANDONING" -> JsFailure.CannotParkWhileAbandoning
        | "ABANDONED" -> JsFailure.Abandoned
        | "CALLABLE_SPEC_INCOMPLETE" -> JsFailure.CallableSpecIncomplete text
        | "CALL_COMMAND_UNSUPPORTED" -> JsFailure.CallCommandUnsupported text
        | "CALL_WAIT_TIMEOUT" -> JsFailure.CallWaitTimeout
        | "CALL_CHILD_UNKNOWN" -> JsFailure.CallChildUnknown(after "no background process with this name: ")
        | "CALL_PARK_UNSUPPORTED" -> JsFailure.CallParkUnsupported
        | "CALL_HOST_UNAVAILABLE" -> JsFailure.CallHostUnavailable
        | "ASK_REFUSED" -> JsFailure.AskRefused text
        | "DELEGATION_UNAVAILABLE" -> JsFailure.DelegationUnavailable
        | "VIEW_DECLARATION_INVALID" -> JsFailure.ViewDeclarationInvalid text
        | _ -> JsFailure.ProgramFailed text
