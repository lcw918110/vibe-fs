namespace Wanxiangshu.Interaction.Dispatch.OpenCode

open Wanxiangshu.OpenCode
open Wanxiangshu.Foundation.HostSession
open Wanxiangshu.Interaction.Admission
open System
open Fable.Core
open Fable.Core.JsInterop
open Wanxiangshu.Foundation
open Wanxiangshu.Foundation.Identity

/// Decodes the raw chat.message hook payload before authority policy sees it.
module PromptIngressCodec =

    [<Emit("typeof $0 === 'string'")>]
    let private isString (value: obj) : bool = jsNative

    [<Emit("typeof $0 === 'boolean'")>]
    let private isBoolean (value: obj) : bool = jsNative

    [<Emit("Array.isArray($0)")>]
    let private isArray (value: obj) : bool = jsNative

    [<Emit("(() => { try { if ($0 === null || typeof $0 !== 'object' || Array.isArray($0)) return false; const p = Object.getPrototypeOf($0); return p === Object.prototype || p === null; } catch { return false; } })()")>]
    let private isPlainRecord (value: obj) : bool = jsNative

    [<Emit("(() => { try { return $0 != null && $1 in Object($0); } catch { return false; } })()")>]
    let private hasProperty (source: obj) (name: string) : bool = jsNative

    [<Emit("(() => { try { return Object.getOwnPropertyDescriptor($0, $1) ?? null; } catch { return null; } })()")>]
    let private propertyDescriptor (source: obj) (name: string) : obj = jsNative

    [<Emit("$0 != null && Object.prototype.hasOwnProperty.call($0, 'value')")>]
    let private isDataDescriptor (descriptor: obj) : bool = jsNative

    [<Emit("$0.value")>]
    let private descriptorValue (descriptor: obj) : obj = jsNative

    type private RawCarrier =
        | MissingRaw
        | MalformedRaw
        | Raw of obj

    type private TextCarrier =
        | MissingText
        | MalformedText
        | Text of string

    type DecodedMessage = ChatAdmissionIntent.DecodedMessage

    let private nonBlankString (value: obj) : string option =
        if isNull value || not (isString value) then
            None
        else
            let text = unbox<string> value
            if String.IsNullOrWhiteSpace text then None else Some text

    let private rawDataProperty (source: obj) (name: string) : RawCarrier =
        let descriptor = propertyDescriptor source name

        if isNull descriptor || not (isDataDescriptor descriptor) then
            MalformedRaw
        else
            Raw(descriptorValue descriptor)

    let private rawProperty (source: obj) (name: string) : RawCarrier =
        if isNull source || not (hasProperty source name) then
            MissingRaw
        elif not (isPlainRecord source) then
            MalformedRaw
        else
            rawDataProperty source name

    let private textValue (value: obj) : TextCarrier =
        match nonBlankString value with
        | Some text -> Text text
        | None -> MalformedText

    let private textProperty (source: obj) (name: string) : TextCarrier =
        match rawProperty source name with
        | MissingRaw -> MissingText
        | MalformedRaw -> MalformedText
        | Raw value -> textValue value

    let private consolidateText (carriers: TextCarrier list) : TextCarrier =
        if carriers |> List.exists ((=) MalformedText) then
            MalformedText
        else
            carriers
            |> List.choose (function
                | Text value -> Some value
                | _ -> None)
            |> List.distinct
            |> function
                | [] -> MissingText
                | [ value ] -> Text value
                | _ -> MalformedText

    let private textOption =
        function
        | Text value -> Some value
        | _ -> None

    let private readString (source: obj) (name: string) : string option = textProperty source name |> textOption

    let private childObject (source: obj) (name: string) : obj =
        match rawProperty source name with
        | Raw value -> value
        | _ -> null

    let private recordValue (allowString: bool) (value: obj) =
        if isPlainRecord value then Raw value
        elif allowString && isString value then MissingRaw
        else MalformedRaw

    let private recordSource (value: obj) =
        if isNull value then MissingRaw else recordValue false value

    let private recordContainer (allowString: bool) (source: obj) (name: string) =
        match rawProperty source name with
        | Raw value -> recordValue allowString value
        | carrier -> carrier

    let private carrierObject =
        function
        | Raw value -> value
        | _ -> null

    let private containerValidity =
        function
        | MalformedRaw -> MalformedText
        | _ -> MissingText

    let private agentCarriers (source: obj) : TextCarrier list =
        let root = recordSource source
        let info = recordContainer false source "info"
        let message = recordContainer false source "message"
        let properties = recordContainer false source "properties"
        let session = recordContainer true source "session"
        let body = recordContainer false source "body"
        let options = recordContainer false source "options"

        let containers =
            [ root
              info
              message
              recordContainer false (carrierObject message) "info"
              properties
              recordContainer false (carrierObject properties) "info"
              session
              recordContainer false (carrierObject session) "info"
              body
              recordContainer false (carrierObject body) "info"
              options
              recordContainer false (carrierObject options) "info" ]

        [ yield! containers |> List.map containerValidity
          yield!
              containers
              |> List.map (fun candidate -> textProperty (carrierObject candidate) "agent") ]

    let private agentOf (source: obj) : string option =
        agentCarriers source |> consolidateText |> textOption

    let private metadataCarrier (source: obj) (key: string) : TextCarrier =
        match rawProperty source "metadata" with
        | MissingRaw -> MissingText
        | MalformedRaw -> MalformedText
        | Raw metadata when isPlainRecord metadata -> textProperty metadata key
        | Raw _ -> MalformedText

    let private parts (source: obj) : obj array =
        match rawProperty source "parts" with
        | Raw value when isArray value -> unbox<obj array> value
        | _ -> [||]

    let private isTrue (value: obj) = isBoolean value && unbox<bool> value

    let private isTrueProperty (source: obj) (name: string) =
        match rawProperty source name with
        | Raw value -> isTrue value
        | _ -> false

    /// The raw facts one host message carries, read from the wire.
    ///
    /// Which of them count as a compaction source is the product's rule
    /// (`HostInternalClassification`); this only reads them.
    let private hostMessageFacts (input: obj) (output: obj) : HostInternalClassification.HostMessageFacts =
        let outputParts = parts output
        let message = childObject output "message"

        let messageParts = parts output

        { HostInternalClassification.HasCompactionPart =
            outputParts
            |> Array.exists (fun part ->
                readString part "type"
                |> Option.exists (fun kind -> kind.Equals("compaction", StringComparison.OrdinalIgnoreCase)))
          HasSummary = isTrueProperty message "summary" || isTrueProperty input "summary"
          AgentIsCompaction =
            ([ input; output; message; childObject input "info" ]
             |> List.choose agentOf
             |> List.exists (fun agent -> agent.Equals("compaction", StringComparison.OrdinalIgnoreCase)))
          ModeIsCompaction =
            ([ readString message "mode"; readString input "mode"; readString (childObject input "info") "mode" ]
             |> List.choose id
             |> List.exists (fun mode -> mode.Equals("compaction", StringComparison.OrdinalIgnoreCase)))
          HasParts = messageParts.Length > 0
          AllPartsSynthetic = Array.forall (fun part -> isTrueProperty part "synthetic") messageParts }

    let private sessionIdCarrierOfPart (sess: obj) : TextCarrier =
        if isNull sess then
            MalformedText
        elif isString sess then
            textValue sess
        else
            [ textProperty sess "id"
              textProperty sess "sessionID"
              textProperty sess "sessionId" ]
            |> consolidateText
            |> function
                | MissingText -> MalformedText
                | carrier -> carrier

    let private sessionCarrier (input: obj) (output: obj) =
        let fromSource (sourceCarrier: RawCarrier) =
            let source = carrierObject sourceCarrier

            let nested =
                match rawProperty source "session" with
                | MissingRaw -> MissingText
                | MalformedRaw -> MalformedText
                | Raw session -> sessionIdCarrierOfPart session

            [ containerValidity sourceCarrier
              textProperty source "sessionID"
              textProperty source "sessionId"
              nested ]

        let message = recordContainer false output "message"
        let info = recordContainer false output "info"

        [ recordSource input; recordSource output; message; info ]
        |> List.collect fromSource
        |> consolidateText

    let private messageIdOf (input: obj) (output: obj) =
        let message = childObject output "message"

        [ readString input "messageID"; readString message "id" ]
        |> List.choose id
        |> List.distinct
        |> function
            | [ physical ] -> Some(PhysicalUserMessageId.create physical)
            | _ -> None

    /// PROMPT-011: read the anchor back from the field the Foundation module owns.
    ///
    /// The field name comes from that module rather than being spelled again here.
    /// Two independent literals for one wire field is how a rename silently breaks
    /// recovery: the write side moves, the read side keeps matching nothing, and
    /// every plugin prompt starts looking like UnknownOrigin.
    let private promptKeyOf (input: obj) (output: obj) =
        [ metadataCarrier input PromptMetadataField.PromptKeyField
          yield!
              parts output
              |> Array.map (fun part -> metadataCarrier part PromptMetadataField.PromptKeyField)
              |> Array.toList ]
        |> consolidateText

    /// Classify one physical message part; synthetic and non-text parts are not
    /// opening content.
    let private textOfPart (part: obj) : string option =
        HostInternalClassification.openingTextOfPart
            (isTrueProperty part "synthetic")
            (readString part "type")
            (readString part "text")

    /// COMPANION-003: the user message's text, for the opening capture. Only the
    /// physical user message's own text parts count — the opening is the first
    /// task prompt, and a synthetic part (tool result, metadata, guidance) is not it.
    let private textOf (output: obj) : string option =
        parts output
        |> Array.choose textOfPart
        |> Array.filter (String.IsNullOrWhiteSpace >> not)
        |> Array.toList
        |> function
            | [] -> None
            | texts -> Some(String.concat "\n" texts)

    let decodeWith (input: obj) (output: obj) : DecodedMessage =
        let message = childObject output "message"
        let info = childObject output "info"
        let properties = childObject output "properties"

        let session = sessionCarrier input output
        let sessionId = session |> textOption |> Option.map SessionId.create
        let promptKey = promptKeyOf input output

        let agent =
            [ input; output; message; info; properties ]
            |> List.collect agentCarriers
            |> consolidateText

        let explicitAgent =
            match agent with
            | Text agent -> Some agent
            | MissingText -> None
            | MalformedText -> None

        let invalidCarrier =
            [ session, ChatAdmissionIntent.IdentityCarrierError.SessionId
              agent, ChatAdmissionIntent.IdentityCarrierError.Agent
              promptKey, ChatAdmissionIntent.IdentityCarrierError.PromptKey ]
            |> List.tryPick (fun (carrier, kind) ->
                match carrier with
                | MalformedText -> Some kind
                | _ -> None)

        let facts = hostMessageFacts input output

        { InvalidIdentityCarrier = invalidCarrier
          SessionId = sessionId
          PhysicalUserMessageId = messageIdOf input output
          ExplicitAgent = explicitAgent
          PromptKey = promptKey |> textOption |> Option.map PromptKey.create
          IsHostCompaction = HostInternalClassification.isCompaction facts
          IsHostSynthetic = HostInternalClassification.isHostSynthetic facts
          Text = textOf output }
