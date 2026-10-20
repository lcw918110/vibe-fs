namespace Wanxiangshu.Repository.Programming.Js

open Wanxiangshu.Foundation.Identity
open Wanxiangshu.Persistence.EventStore

type JsTransactionId = private JsTransactionId of string

module JsTransactionId =
    val create: value: string -> JsTransactionId
    val value: JsTransactionId -> string
    val generate: unit -> JsTransactionId

[<RequireQualifiedAccess>]
type JsTransactionAppendPhase =
    | Prepared
    | Committed

type JsTransactionAppendFailure =
    { Phase: JsTransactionAppendPhase
      TransactionId: JsTransactionId
      EventId: EventId
      Error: AppendError }

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
    val code: failure: JsFailure -> string
    val reason: failure: JsFailure -> string
    val render: failure: JsFailure -> string
    val ofWire: code: string -> reason: string -> JsFailure
