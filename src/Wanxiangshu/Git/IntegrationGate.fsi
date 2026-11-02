namespace Wanxiangshu.Git

open System
open System.Threading.Tasks

/// Cross-process publish serialization represented as one disposable resource.
type IntegrationGate =
    new: lease: obj -> IntegrationGate
    member Release: unit -> Task<unit>
    interface IAsyncDisposable

module IntegrationGate =
    val lockPath: repoPath: string -> branch: string -> string
    val acquire: path: string -> Task<IntegrationGate>
