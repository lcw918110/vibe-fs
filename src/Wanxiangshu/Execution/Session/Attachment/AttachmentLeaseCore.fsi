namespace Wanxiangshu.Execution.Session.Attachment

open Wanxiangshu.Execution.Session
open Wanxiangshu.Foundation.Identity

[<RequireQualifiedAccess>]
module AttachmentLeaseCore =
    val kindKey: kind: AttachmentKind -> string
    val optionalText: value: string option -> obj
    val resultToNative: encode: ('T -> obj) -> result: Result<'T, string> -> obj
    val resultFromNative: decode: (obj -> 'T) -> result: obj -> Result<'T, string>

type AttachmentLeaseRegistry =
    new: unit -> AttachmentLeaseRegistry
    member Sessions: obj
    member TryFindByChild: child: SessionId -> (SessionId * AttachmentKind) list
    member Snapshot: unit -> (SessionId * AttachmentKind) list
