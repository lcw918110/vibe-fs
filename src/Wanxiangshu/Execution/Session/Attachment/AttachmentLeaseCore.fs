namespace Wanxiangshu.Execution.Session.Attachment

open Fable.Core
open Fable.Core.JsInterop
open Wanxiangshu.Execution.Session
open Wanxiangshu.Foundation.Identity

[<RequireQualifiedAccess>]
module AttachmentLeaseCore =

    let kindKey (kind: AttachmentKind) =
        match kind with
        | AttachmentKind.Companion -> "companion"
        | AttachmentKind.SyncInspector -> "sync-inspector"
        | AttachmentKind.SyncCoder -> "sync-coder"
        | AttachmentKind.SyncEngineer -> "sync-engineer"
        | AttachmentKind.SyncDevOps -> "sync-devops"
        | AttachmentKind.Bookkeeper transactionId -> "bookkeeper\u001f" + transactionId

    let optionalText (value: string option) : obj =
        match value with
        | Some text -> box text
        | None -> null

    let resultToNative (encode: 'T -> obj) (result: Result<'T, string>) : obj =
        match result with
        | Ok value -> createObj [ "ok" ==> true; "value" ==> encode value ]
        | Error error -> createObj [ "ok" ==> false; "error" ==> error ]

    let resultFromNative (decode: obj -> 'T) (result: obj) : Result<'T, string> =
        if unbox<bool> result?ok then
            Ok(decode result?value)
        else
            Error(unbox<string> result?error)

module private RegistryMapping =

    [<Import("AttachmentLeaseRegistry", "wanxiangli/attachment-lease")>]
    let registryClass: obj = jsNative

    [<Import("AttachmentSessions", "wanxiangli/attachment-lease")>]
    let sessionsClass: obj = jsNative

    let fragmentKinds =
        [ AttachmentKind.Companion
          AttachmentKind.SyncInspector
          AttachmentKind.SyncCoder
          AttachmentKind.SyncEngineer
          AttachmentKind.SyncDevOps ]

    let parseKind (kindText: string) : AttachmentKind option =
        match kindText.StartsWith("bookkeeper\u001f") with
        | true -> Some(AttachmentKind.Bookkeeper(kindText.Substring("bookkeeper\u001f".Length)))
        | false ->
            fragmentKinds
            |> List.tryFind (fun candidate -> AttachmentLeaseCore.kindKey candidate = kindText)

    let decodeOwnerPair (item: obj) : (SessionId * AttachmentKind) option =
        let ownerText = unbox<string> item?owner
        let kindText = unbox<string> item?kind
        parseKind kindText |> Option.map (fun kind -> SessionId.create ownerText, kind)

    let decodeOwnerPairs (items: obj array) : (SessionId * AttachmentKind) list =
        items |> Array.choose decodeOwnerPair |> Array.toList

open RegistryMapping

type AttachmentLeaseRegistry() =
    let store: obj = emitJsExpr registryClass "new $0()"
    let sessions: obj = emitJsExpr (sessionsClass, store) "new $0($1)"

    member _.Sessions: obj = sessions

    member _.TryFindByChild(child: SessionId) : (SessionId * AttachmentKind) list =
        let items: obj array = store?tryFindByChild (SessionId.value child)
        decodeOwnerPairs items

    member _.Snapshot() : (SessionId * AttachmentKind) list =
        let items: obj array = store?snapshot ()
        decodeOwnerPairs items
