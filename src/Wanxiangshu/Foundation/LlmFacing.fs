namespace Wanxiangshu.Foundation

open Fable.Core

/// F# 调用边界；文档、值编码和布局由 wanxiangli/llm-text 拥有。
[<RequireQualifiedAccess>]
module LlmFacing =
    type DataBlock = interface end
    type Document = interface end

    [<RequireQualifiedAccess>]
    module DataTypes =
        type Value = interface end
        type Field = interface end

    type private Library =
        abstract empty: unit -> Document
        abstract instructions: string array -> Document
        abstract instruction: string -> Document
        abstract instructionsOnly: Document -> Document
        abstract withInstructions: string array * Document -> Document
        abstract withInstruction: string * Document -> Document
        abstract withData: DataBlock array * Document -> Document
        abstract combine: Document array -> Document
        abstract render: Document -> string
        abstract renderInstruction: string -> string
        abstract renderInstructions: string array -> string
        abstract normalizeNewlines: string -> string
        abstract byteCount: string -> int
        abstract ofJs: obj -> DataTypes.Value
        abstract structuredValue: DataTypes.Value -> DataBlock array
        abstract rootStructuredValue: DataTypes.Value -> DataBlock array
        abstract stringField: string * string -> DataBlock
        abstract intField: string * int -> DataBlock
        abstract int64Field: string * int64 -> DataBlock
        abstract boolField: string * bool -> DataBlock
        abstract stringMember: string * string -> DataTypes.Field
        abstract intMember: string * int -> DataTypes.Field
        abstract int64Member: string * int64 -> DataTypes.Field
        abstract floatMember: string * float -> DataTypes.Field
        abstract boolMember: string * bool -> DataTypes.Field
        abstract table: string * DataTypes.Field array -> DataBlock
        abstract tableArray: string * DataTypes.Field array -> DataBlock
        abstract textTables: string * string array -> DataBlock array

    [<Import("LlmText", "wanxiangli/llm-text")>]
    let private libraryClass: obj = jsNative

    [<Emit("new $0()")>]
    let private construct (cls: obj) : Library = jsNative

    let private library = construct libraryClass
    let empty = library.empty ()

    let instructions texts =
        library.instructions (List.toArray texts)

    let instruction text = library.instruction text
    let instructionsOnly document = library.instructionsOnly document

    let withInstructions texts document =
        library.withInstructions (List.toArray texts, document)

    let withInstruction text document =
        library.withInstruction (text, document)

    let withData blocks document =
        library.withData (List.toArray blocks, document)

    let combine documents =
        library.combine (List.toArray documents)

    let render document = library.render document
    let renderInstruction text = library.renderInstruction text

    let renderInstructions texts =
        library.renderInstructions (List.toArray texts)

    let normalizeNewlines text = library.normalizeNewlines text
    let byteCount text = library.byteCount text

    [<RequireQualifiedAccess>]
    module Data =
        type Value = DataTypes.Value
        type Field = DataTypes.Field
        let ofJs (value: obj) : Value = library.ofJs value

        let structuredValue value =
            library.structuredValue value |> List.ofArray

        let rootStructuredValue value =
            library.rootStructuredValue value |> List.ofArray

        let stringField name value = library.stringField (name, value)
        let intField name value = library.intField (name, value)
        let int64Field name value = library.int64Field (name, value)
        let boolField name value = library.boolField (name, value)
        let stringMember name value = library.stringMember (name, value)
        let intMember name value = library.intMember (name, value)
        let int64Member name value = library.int64Member (name, value)
        let floatMember name value = library.floatMember (name, value)
        let boolMember name value = library.boolMember (name, value)

        let table name fields =
            library.table (name, List.toArray fields)

        let tableArray name fields =
            library.tableArray (name, List.toArray fields)

        let textTables name texts =
            library.textTables (name, List.toArray texts) |> List.ofArray
