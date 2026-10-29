namespace Wanxiangshu.OpenCode

open System.Threading.Tasks
open Wanxiangshu.Foundation
open Wanxiangshu.Foundation.Identity
open Wanxiangshu.Host

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
    member OptionalTexts: name: string -> string list option
    member Texts: name: string -> string list
    member OptionalNumber: name: string -> float option
    member OptionalNonNegativeInteger: name: string -> Result<int option, unit>
    member OptionalNonNegativeNumber: name: string -> Result<float option, unit>
    member Raw: obj

type HostToolFactory = private HostToolFactory of obj
type HostSchema = private HostSchema of obj

type ToolSpec =
    { Name: string
      Description: string
      Arguments: (string * HostSchema) list
      Execute: HostToolArguments -> HostToolContext -> Task<string> }

module ToolHostCodec =
    val newHandleId: unit -> string
    val decodeContext: raw: obj -> HostToolContext
    val hookCallId: raw: obj -> ToolCallId option
    val factory: toolModule: obj -> HostToolFactory
    /// Construct host tool arguments from an in-process raw object.
    val argumentsOfRaw: raw: obj -> HostToolArguments
    val internal schemaValue: schema: HostSchema -> obj
    val stringSchema: factory: HostToolFactory -> HostSchema
    val anySchema: factory: HostToolFactory -> HostSchema
    val stringSchemaDescribed: description: string -> factory: HostToolFactory -> HostSchema
    val numberSchema: factory: HostToolFactory -> HostSchema
    val numberSchemaDescribed: description: string -> factory: HostToolFactory -> HostSchema

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
    val nonNegativeIntegerSchemaDescribed: description: string -> factory: HostToolFactory -> HostSchema
    val optionalNonNegativeNumberSchemaDescribed: description: string -> factory: HostToolFactory -> HostSchema
    val nonNegativeNumberSchemaDescribed: description: string -> factory: HostToolFactory -> HostSchema
    val optionalStringArraySchema: factory: HostToolFactory -> HostSchema
    val stringArraySchema: factory: HostToolFactory -> HostSchema
    val scopeAnnotationSchema: factory: HostToolFactory -> HostSchema
    val objectSchemaOf: fields: (string * HostSchema) list -> factory: HostToolFactory -> HostSchema

    /// Optional object schema: the caller may omit the whole object.
    val optionalObjectSchema: fields: (string * HostSchema) list -> factory: HostToolFactory -> HostSchema

    val arraySchemaOf: item: HostSchema -> description: string -> factory: HostToolFactory -> HostSchema

    val carrySchema: description: string -> factory: HostToolFactory -> HostSchema

    val register: factory: HostToolFactory -> spec: ToolSpec -> obj
    val registry: factory: HostToolFactory -> specs: ToolSpec list -> obj
    val hide: registry: obj -> name: string -> callback: 'callback -> unit

    val looksLikeHandleId: value: string -> bool
    val digest: text: string -> string
