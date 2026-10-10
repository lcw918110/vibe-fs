namespace Wanxiangshu.OpenCode.Host

open System
open Fable.Core
open Fable.Core.JsInterop
open Wanxiangshu.Foundation.Identity

type ProtocolArgumentCall =
    { SessionId: SessionId
      ToolCallId: ToolCallId
      Tool: string }

[<RequireQualifiedAccess>]
type HiddenProtocolArguments =
    | NotHidden
    | SameCall
    | DifferentCallOrChangedArguments

/// host-boundary-032: the object-local protocol argument stash shared by the
/// manager review contract and the read-only delegation contract.
///
/// Each contract owns one static StashSpec: a private Symbol singleton, the
/// hidden field table, the field set whose reappearance marks a changed call,
/// and the loud failure messages. hide saves the fields' original property
/// descriptors (or undefined) plus the current key order under the Symbol and
/// deletes the fields; restore puts the same descriptors back in the saved
/// order and removes the Symbol. A failed field deletion either deletes the
/// Symbol record again or leaves it, per the explicit spec option, so the two
/// contracts keep their current, different observable behavior.
///
/// This is not ProtocolArgumentVault: the vault keeps wire argument evidence
/// keyed by (sessionId, callId) for the provider-facing transform, while this
/// stash lives and dies with one arguments object between the before and the
/// matching after callback. There is no decorator container, dynamic registry
/// or central runtime.
module ProtocolArgumentStash =

    type StashField = { Name: string; SavedKey: string }

    type StashSpec =
        { Symbol: obj
          Fields: StashField list
          ReappearanceFields: string list
          HoldMessage: string
          RestoreOrderMessage: string
          RestoreFrozenMessage: string
          FieldDeleteFailure: string -> string
          RestoreDeleteFailure: string -> string
          SymbolDeleteFailure: string
          CompensateDeleteFailure: bool }

    [<Emit("typeof $0 === 'object' && $0 !== null && !Array.isArray($0)")>]
    let private isPlainObject (value: obj) : bool = jsNative

    [<Emit("Object.prototype.hasOwnProperty.call($0, $1)")>]
    let private hasOwn (target: obj) (key: obj) : bool = jsNative

    [<Emit("Object.isExtensible($0)")>]
    let private isExtensible (target: obj) : bool = jsNative

    [<Emit("Object.getOwnPropertyDescriptor($0, $1)")>]
    let private getOwnPropertyDescriptor (target: obj) (key: obj) : obj = jsNative

    [<Emit("Object.getOwnPropertyNames($0)")>]
    let private ownPropertyNames (target: obj) : string array = jsNative

    [<Emit("Object.defineProperty($0, $1, $2)")>]
    let private defineProperty (target: obj) (key: obj) (descriptor: obj) : unit = jsNative

    [<Emit("Reflect.deleteProperty($0, $1)")>]
    let private deleteProperty (target: obj) (key: obj) : bool = jsNative

    [<Emit("throw new TypeError($0)")>]
    let private throwTypeError (message: string) : unit = jsNative

    let private isDescriptorConfigurable (descriptor: obj) : bool =
        if isNull descriptor then
            true
        else
            let conf = descriptor?configurable
            not (isNull conf) && unbox<bool> conf

    let private isStashField (spec: StashSpec) (key: string) : bool =
        spec.Fields |> List.exists (fun field -> field.Name = key)

    let private fieldsArray (spec: StashSpec) : StashField array = Array.ofList spec.Fields

    /// The saved key order starts at the first hidden field and runs to the
    /// end of the own property names; a missing field table contributes no keys.
    let private captureKeyOrder (spec: StashSpec) (args: obj) : string array =
        ownPropertyNames args
        |> Array.skipWhile (fun key -> not (isStashField spec key))

    let private requireRestorable (spec: StashSpec) (args: obj) (keys: string array) : unit =
        let canRestore =
            keys
            |> Array.forall (fun key -> isDescriptorConfigurable (getOwnPropertyDescriptor args key))

        if not canRestore then
            throwTypeError spec.RestoreOrderMessage

    let private deleteFieldOrThrow (message: string -> string) (args: obj) (field: string) : unit =
        let deleted = deleteProperty args field

        if not deleted then
            throwTypeError (message field)

    let private deleteExistingField (message: string -> string) (args: obj) (field: string) : unit =
        if not (isNull (getOwnPropertyDescriptor args field)) then
            deleteFieldOrThrow message args field

    let private deleteAllStashedFields (spec: StashSpec) (args: obj) (descriptors: (StashField * obj) array) : unit =
        for field, _ in descriptors do
            deleteExistingField spec.FieldDeleteFailure args field.Name

    let private compensateDeleteFailure (spec: StashSpec) (args: obj) : unit =
        if spec.CompensateDeleteFailure then
            deleteProperty args spec.Symbol |> ignore

    let private hideStashFields (spec: StashSpec) (args: obj) : unit =
        let fields = fieldsArray spec

        let descriptors =
            fields
            |> Array.map (fun field -> field, getOwnPropertyDescriptor args field.Name)

        let canHold =
            isExtensible args
            && descriptors
               |> Array.forall (fun (_, descriptor) -> isDescriptorConfigurable descriptor)

        if not canHold then
            throwTypeError spec.HoldMessage

        let keyOrder = captureKeyOrder spec args
        requireRestorable spec args keyOrder

        let saved =
            createObj (
                [ for field, descriptor in descriptors -> field.SavedKey, descriptor ]
                @ [ "keyOrder", box keyOrder ]
            )

        let symbolDescriptor =
            createObj [ "value", saved; "enumerable", box false; "configurable", box true ]

        defineProperty args spec.Symbol symbolDescriptor

        try
            deleteAllStashedFields spec args descriptors
        with ex ->
            compensateDeleteFailure spec args
            raise ex

    let hide (spec: StashSpec) (args: obj) : unit =
        if isNull args || not (isPlainObject args) then
            throwTypeError "Tool arguments must be an object"

        if not (hasOwn args spec.Symbol) then
            hideStashFields spec args

    let private hasSavedArguments (spec: StashSpec) (args: obj) =
        not (isNull args) && isPlainObject args && hasOwn args spec.Symbol

    let private hasExactCallIdentity (owner: ProtocolArgumentCall) =
        not (String.IsNullOrWhiteSpace(SessionId.value owner.SessionId))
        && not (String.IsNullOrWhiteSpace(ToolCallId.value owner.ToolCallId))
        && not (String.IsNullOrWhiteSpace owner.Tool)

    let private bindCallOwner (spec: StashSpec) (owner: ProtocolArgumentCall option) (args: obj) =
        match owner with
        | Some call when hasExactCallIdentity call ->
            let saved = args?(spec.Symbol)
            saved?owner <- box call
        | _ -> ()

    let hideForCall (spec: StashSpec) (owner: ProtocolArgumentCall option) (args: obj) : unit =
        let alreadyHidden = hasSavedArguments spec args
        hide spec args

        if not alreadyHidden then
            bindCallOwner spec owner args

    let private classifySavedCall (owner: ProtocolArgumentCall option) (saved: obj) =
        match owner with
        | Some call when
            hasExactCallIdentity call
            && hasOwn saved "owner"
            && call = unbox<ProtocolArgumentCall> saved?owner
            ->
            HiddenProtocolArguments.SameCall
        | _ -> HiddenProtocolArguments.DifferentCallOrChangedArguments

    let classifyHiddenArguments
        (spec: StashSpec)
        (owner: ProtocolArgumentCall option)
        (args: obj)
        : HiddenProtocolArguments =
        if not (hasSavedArguments spec args) then
            HiddenProtocolArguments.NotHidden
        elif spec.ReappearanceFields |> List.exists (fun field -> hasOwn args field) then
            HiddenProtocolArguments.DifferentCallOrChangedArguments
        else
            classifySavedCall owner args?(spec.Symbol)

    let private restoredDescriptor (spec: StashSpec) (args: obj) (saved: obj) (key: string) =
        match spec.Fields |> List.tryFind (fun field -> field.Name = key) with
        | Some field -> saved?(field.SavedKey)
        | None -> getOwnPropertyDescriptor args key

    let private restoreStashFields (spec: StashSpec) (args: obj) (saved: obj) : unit =
        let keyOrder: string array = saved?keyOrder

        let affectedKeys =
            Array.append keyOrder (fieldsArray spec |> Array.map (fun field -> field.Name))
            |> Array.distinct

        requireRestorable spec args affectedKeys

        let descriptors =
            keyOrder
            |> Array.map (fun key -> key, restoredDescriptor spec args saved key)
            |> Array.filter (fun (_, descriptor) -> not (isNull descriptor))

        for key in affectedKeys do
            deleteExistingField spec.RestoreDeleteFailure args key

        for key, descriptor in descriptors do
            defineProperty args key descriptor

    let private restoreSavedArguments (spec: StashSpec) (args: obj) : unit =
        let saved = args?(spec.Symbol)

        if not (isExtensible args) then
            throwTypeError spec.RestoreFrozenMessage

        restoreStashFields spec args saved

        let deletedKey = deleteProperty args spec.Symbol

        if not deletedKey then
            throwTypeError spec.SymbolDeleteFailure

    let restore (spec: StashSpec) (args: obj) : unit =
        if hasSavedArguments spec args then
            restoreSavedArguments spec args

    let private ownsSavedCall (spec: StashSpec) (owner: ProtocolArgumentCall option) (saved: obj) =
        (owner.IsNone && not (hasOwn saved "owner"))
        || classifySavedCall owner saved = HiddenProtocolArguments.SameCall

    let restoreForCall (spec: StashSpec) (owner: ProtocolArgumentCall option) (args: obj) : unit =
        if hasSavedArguments spec args && ownsSavedCall spec owner args?(spec.Symbol) then
            restoreSavedArguments spec args
