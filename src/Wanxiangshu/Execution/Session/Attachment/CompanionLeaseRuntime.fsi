namespace Wanxiangshu.Execution.Session.Attachment

open System.Threading.Tasks
open Wanxiangshu.Execution.Session
open Wanxiangshu.Foundation
open Wanxiangshu.Foundation.Identity
open Wanxiangshu.Foundation.HostSession

type SatelliteOrigin =
    | Created
    | Reused
    | Replacement

type SatelliteLease =
    { SessionId: SessionId
      Origin: SatelliteOrigin }

type SatelliteSpec =
    { Kind: SatelliteKind
      Agent: string
      Title: string
      Directory: string option
      RestoredSessionId: SessionId option
      Link: SessionId -> SessionId -> string -> Task<Result<unit, string>>
      Close: SessionId -> Task<Result<unit, string>> }

type CompanionLeaseRuntime =
    new: sessions: ISessionHostPort * registry: AttachmentLeaseRegistry -> CompanionLeaseRuntime
    member Ensure: owner: SessionId * spec: SatelliteSpec -> Task<Result<SatelliteLease, string>>
    member Retire: owner: SessionId * spec: SatelliteSpec -> Task<Result<unit, string>>
