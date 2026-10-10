namespace Wanxiangshu.OpenCode

open System.Threading.Tasks
open Wanxiangshu.Foundation
open Wanxiangshu.Foundation.Identity

type HostSessionObservation =
    { SessionId: SessionId
      HasParent: bool
      Agent: string option }

module HostIngressCodec =
    val sessionObservation: raw: obj -> HostSessionObservation option
    val sessionAgent: raw: obj -> string option

type HostToolArguments =
    internal new: raw: obj -> HostToolArguments
    member Text: name: string -> string
    member OptionalText: name: string -> string option
    member OptionalTextIn: objectName: string * propertyName: string -> string option
    member OptionalTexts: name: string -> string list option
    member Texts: name: string -> string list
    member OptionalNumber: name: string -> float option
    member OptionalNonNegativeInteger: name: string -> Result<int option, unit>
    member Raw: obj
    member OptionalBool: name: string -> bool option

type HostToolContext =
    { SessionId: string
      Agent: string option
      ToolCallId: ToolCallId option
      ProviderRunId: ProviderRunIdentity option
      PromptText: string option
      AttachAbort: (unit -> unit) -> (unit -> unit) }

type HostToolFactory = private HostToolFactory of obj
type HostSchema = private HostSchema of obj

[<RequireQualifiedAccess>]
type ToolAdmission =
    | OfficeRole of (HostToolContext -> Role -> bool)
    | PrivateAttachment of (HostToolContext -> bool)

type ToolSpec =
    { Name: string
      Description: string
      Arguments: (string * HostSchema) list
      Admission: ToolAdmission
      Execute: HostToolArguments -> HostToolContext -> Task<string> }

module ToolHostCodec =
    val newHandleId: unit -> string
    val decodeContext: raw: obj -> HostToolContext
    val hookCallId: raw: obj -> ToolCallId option
    val factory: toolModule: obj -> HostToolFactory
    val internal schemaValue: schema: HostSchema -> obj
    val stringSchema: factory: HostToolFactory -> HostSchema
    val stringSchemaDescribed: description: string -> factory: HostToolFactory -> HostSchema
    val numberSchema: factory: HostToolFactory -> HostSchema
    val numberSchemaDescribed: description: string -> factory: HostToolFactory -> HostSchema

    val boundedIntegerSchema:
        minimum: int -> maximum: int -> description: string -> factory: HostToolFactory -> HostSchema

    val optionalBoundedIntegerSchema:
        minimum: int -> maximum: int -> description: string -> factory: HostToolFactory -> HostSchema

    val boolSchema: factory: HostToolFactory -> HostSchema
    val boolSchemaDescribed: description: string -> factory: HostToolFactory -> HostSchema
    val enumSchema: values: string list -> factory: HostToolFactory -> HostSchema
    val enumSchemaDescribed: values: string list -> description: string -> factory: HostToolFactory -> HostSchema
    val optionalEnumSchema: values: string list -> factory: HostToolFactory -> HostSchema

    val optionalEnumSchemaDescribed:
        values: string list -> description: string -> factory: HostToolFactory -> HostSchema

    val managedOrHandleSchema: values: string list -> factory: HostToolFactory -> HostSchema
    val optionalStringSchema: factory: HostToolFactory -> HostSchema
    val optionalStringSchemaDescribed: description: string -> factory: HostToolFactory -> HostSchema
    val optionalNumberSchema: factory: HostToolFactory -> HostSchema
    val optionalNonNegativeIntegerSchemaDescribed: description: string -> factory: HostToolFactory -> HostSchema
    val optionalStringArraySchema: factory: HostToolFactory -> HostSchema
    val stringArraySchema: factory: HostToolFactory -> HostSchema
    val todoArraySchemaDescribed: description: string -> factory: HostToolFactory -> HostSchema
    val objectSchemaOf: fields: (string * HostSchema) list -> factory: HostToolFactory -> HostSchema
    val arraySchemaOf: item: HostSchema -> description: string -> factory: HostToolFactory -> HostSchema
    val register: factory: HostToolFactory -> spec: ToolSpec -> obj
    val registry: factory: HostToolFactory -> specs: ToolSpec list -> obj
    val hide: registry: obj -> name: string -> callback: 'callback -> unit

    type TomlValue =
        | TString of string
        | TInt of int
        | TInt64 of int64
        | TBool of bool
        | TTable of (string * TomlValue) list

    val tomlObject: fields: (string * TomlValue) list -> string
    val tomlObjectWithInstructions: instructions: string list -> fields: (string * TomlValue) list -> string
    val tomlTable: name: string -> entries: (string * TomlValue) list list -> string
    val looksLikeHandleId: value: string -> bool
    val digest: text: string -> string
