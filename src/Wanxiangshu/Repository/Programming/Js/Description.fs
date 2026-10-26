namespace Wanxiangshu.Repository.Programming.Js

open Wanxiangshu.Foundation


/// Canonical example with capability requirements (proposal §44).
type JsExample =
    { Requires: Set<JsCapability>
      Source: string }

/// Proposal §12/§16/§31–§47: LLM-visible description is header + exact public
/// base class + capability rules + one Ultra Example + footer. Runtime proxy
/// class stays out of the description. Prose is already-localized (PROMPT-019).
module JsCanonicalDescription =

    [<RequireQualifiedAccess>]
    module Path =
        [<Literal>]
        let Header = "tool/js-program/header"

        [<Literal>]
        let Footer = "tool/js-program/footer"

        [<Literal>]
        let Contract = "tool/js-program/contract"

        [<Literal>]
        let ContractParallelEdits = "tool/js-program/contract-parallel-edits"

        [<Literal>]
        let ContractParallelReads = "tool/js-program/contract-parallel-reads"

        [<Literal>]
        let VerbRead = "tool/js-program/verb-read"

        [<Literal>]
        let VerbCreate = "tool/js-program/verb-create"

        [<Literal>]
        let ReadRules = "tool/js-program/rules-read"

        [<Literal>]
        let ReviewOnlyRules = "tool/js-program/review-only"

        [<Literal>]
        let Recipes = "tool/js-program/recipes"

        [<Literal>]
        let WriteRules = "tool/js-program/rules-write"

        [<Literal>]
        let MutationRules = "tool/js-program/rules-mutation"

        [<Literal>]
        let Rm = "tool/js-program/rm"

        [<Literal>]
        let Mv = "tool/js-program/mv"

        [<Literal>]
        let RunRules = "tool/js-program/run-rules"

        [<Literal>]
        let ViewDeclarationExtra = "tool/js-program/view-declaration-extra"

        [<Literal>]
        let Deliver = "tool/js-program/deliver"

        [<Literal>]
        let DelegationRules = "tool/js-program/delegation-rules"

        [<Literal>]
        let UltraFraming = "tool/js-program/ultra-framing"

        [<Literal>]
        let UltraUnavailable = "tool/js-program/ultra-unavailable"

        [<Literal>]
        let UltraEngineering = "tool/js-program/ultra-engineering"

        [<Literal>]
        let UltraInvestigation = "tool/js-program/ultra-investigation"

        [<Literal>]
        let UltraDevOps = "tool/js-program/ultra-devops"

        [<Literal>]
        let UltraManagerInvestigation = "tool/js-program/ultra-manager-investigation"

        [<Literal>]
        let UltraPlan = "tool/js-program/ultra-plan"

        [<Literal>]
        let MechanicalSemantic = "tool/js-program/mechanical-semantic"

        [<Literal>]
        let ReasonRunUnimplemented = "tool/js-program/reason-run-unimplemented"

        [<Literal>]
        let ArgProgram = "tool/js-program/arg-program"

        [<Literal>]
        let MissingProgram = "tool/js-program/missing-program"

        [<Literal>]
        let HookNotVisible = "tool/js-program/hook-not-visible"

        [<Literal>]
        let Park = "tool/js-program/park"

        [<Literal>]
        let ParkedPayload = "tool/js-program/parked-payload"

        [<Literal>]
        let BaseClass = "tool/js-program/base-class"

        [<Literal>]
        let BaseClassRead = "tool/js-program/base-class-read"

        [<Literal>]
        let BaseClassAnchors = "tool/js-program/base-class-anchors"

        [<Literal>]
        let BaseClassGlob = "tool/js-program/base-class-glob"

        [<Literal>]
        let BaseClassGrep = "tool/js-program/base-class-grep"

        [<Literal>]
        let BaseClassWrite = "tool/js-program/base-class-write"

        [<Literal>]
        let BaseClassRemove = "tool/js-program/base-class-remove"

        [<Literal>]
        let BaseClassMove = "tool/js-program/base-class-move"

        [<Literal>]
        let BaseClassExec = "tool/js-program/base-class-exec"

        [<Literal>]
        let BaseClassFork = "tool/js-program/base-class-fork"

        [<Literal>]
        let BaseClassResume = "tool/js-program/base-class-resume"

        [<Literal>]
        let BaseClassCommission = "tool/js-program/base-class-commission"

        [<Literal>]
        let BaseClassDevOps = "tool/js-program/base-class-devops"

        [<Literal>]
        let BaseClassFission = "tool/js-program/base-class-fission"

        [<Literal>]
        let BaseClassHorizon = "tool/js-program/base-class-horizon"

        [<Literal>]
        let BaseClassAsk = "tool/js-program/base-class-ask"

        [<Literal>]
        let BaseClassReview = "tool/js-program/base-class-review"

        [<Literal>]
        let BaseClassLifecycle = "tool/js-program/base-class-lifecycle"

        /// The Plan role's own surface prose (the draft path, the DevOps
        /// delegation shape, the read-only-until-review rule). It is a
        /// separate resource tree because it only describes the Plan
        /// surface, and it is rendered for the Plan role alone.
        [<Literal>]
        let JsPlan = "tool/js-plan"

    type Prose =
        { Header: string

          Footer: string
          Contract: string

          ContractParallelEdits: string
          ContractParallelReads: string
          VerbRead: string

          VerbCreate: string
          ReadRules: string
          ReviewOnlyRules: string
          Recipes: string

          WriteRules: string
          MutationRules: string
          Rm: string
          Mv: string
          RunRules: string
          Deliver: string
          DelegationRules: string
          UltraFraming: string
          UltraUnavailable: string
          UltraEngineering: string
          UltraInvestigation: string
          UltraDevOps: string
          UltraManagerInvestigation: string
          UltraPlan: string
          MechanicalSemantic: string

          ReasonRunUnimplemented: string

          Park: string
          ParkedPayload: string
          BaseClass: string
          BaseClassRead: string
          BaseClassAnchors: string
          BaseClassGlob: string
          BaseClassGrep: string
          BaseClassWrite: string
          BaseClassRemove: string
          BaseClassMove: string
          BaseClassExec: string
          BaseClassFork: string
          BaseClassResume: string
          BaseClassCommission: string
          BaseClassDevOps: string
          BaseClassFission: string
          BaseClassHorizon: string
          BaseClassAsk: string
          BaseClassReview: string
          BaseClassLifecycle: string

          JsPlan: string }

    let private fill (template: string) (pairs: (string * string) list) =
        let replaced =
            (template, pairs)
            ||> List.fold (fun acc (key, value) -> acc.Replace("{{" + key + "}}", value))

        if replaced.Contains("{{") then
            invalidOp "PROMPT-019:unsubstituted-placeholder"

        replaced

    let private runStub (prose: Prose) =
        fill
            """  async run() {
    throw new Error("{{reason_run_unimplemented}}");
  }"""
            [ "reason_run_unimplemented", prose.ReasonRunUnimplemented ]

    let has (capabilities: Set<JsCapability>) (capability: JsCapability) = Set.contains capability capabilities

    /// Whether the library member directory (wanxiangli/program
    /// `membersFor`) projects `memberName` for this capability set. The public
    /// and runtime base classes gate every member on this one directory, so the
    /// shown tutorial, the executed class and the schema member list cannot
    /// disagree about which members exist.
    let private present (capabilities: Set<JsCapability>) (memberName: string) =
        Array.contains memberName (JsCapability.memberNamesFor capabilities "")

    let private classOpen =
        """
class JsProgram {"""
            .TrimStart()

    let private reviewCalls (text: string) =
        text
            .Replace("this.cat(", "this.reviewOnly.cat(")
            .Replace("this.stat(", "this.reviewOnly.stat(")
            .Replace("this.ls(", "this.reviewOnly.ls(")
            .Replace("this.rg(", "this.reviewOnly.rg(")

    let publicBaseClass (prose: Prose) (capabilities: Set<JsCapability>) : string =
        fill
            prose.BaseClass
            [ "baseClassRead",
              if present capabilities "cat" then
                  prose.BaseClassRead + "\n" + prose.BaseClassAnchors
              else
                  ""
              "baseClassGlob",
              if present capabilities "ls" then
                  prose.BaseClassGlob
              else
                  ""
              "baseClassGrep",
              if present capabilities "rg" then
                  prose.BaseClassGrep
              else
                  ""
              "baseClassWrite",
              if present capabilities "write" then
                  prose.BaseClassWrite
              else
                  ""
              "baseClassRemove",
              if present capabilities "rm" then
                  prose.BaseClassRemove
              else
                  ""
              "baseClassMove",
              if present capabilities "mv" then
                  prose.BaseClassMove
              else
                  ""
              "baseClassExec",
              if present capabilities "call" then
                  prose.BaseClassExec
              else
                  ""
              "baseClassFork",
              if present capabilities "fork" then
                  prose.BaseClassFork
              else
                  ""
              "baseClassResume",
              if present capabilities "resume" then
                  prose.BaseClassResume
              else
                  ""
              "baseClassCommission",
              if present capabilities "commission" then
                  prose.BaseClassCommission
              else
                  ""
              "baseClassDevOps",
              if present capabilities "devops" then
                  prose.BaseClassDevOps
              else
                  ""
              "baseClassFission",
              if present capabilities "fission" then
                  prose.BaseClassFission
              else
                  ""
              "baseClassHorizon",
              if present capabilities "horizon" then
                  prose.BaseClassHorizon
              else
                  ""
              "baseClassAsk",
              if present capabilities "ask" then
                  prose.BaseClassAsk
              else
                  ""
              "baseClassReview",
              if present capabilities "review" then
                  prose.BaseClassReview
              else
                  ""
              "baseClassLifecycle", prose.BaseClassLifecycle ]

    let private publicReviewClass prose capabilities =
        String.concat
            "\n"
            [ "class ReviewOnly {"
              if present capabilities "cat" then
                  reviewCalls prose.BaseClassRead
              if present capabilities "ls" then
                  reviewCalls prose.BaseClassGlob
              if present capabilities "rg" then
                  reviewCalls prose.BaseClassGrep
              "}\n" ]

    let private publicBaseClassForRole prose (roleName: string) capabilities =
        let scoped =
            { prose with
                BaseClassRead = ""
                BaseClassGlob = ""
                BaseClassGrep = ""
                BaseClassLifecycle = "  reviewOnly = new ReviewOnly();\n" + prose.BaseClassLifecycle }

        if roleName.Trim().ToLowerInvariant() = "manager" then
            publicReviewClass prose capabilities + publicBaseClass scoped capabilities
        else
            publicBaseClass prose capabilities

    let private exampleSourceForRole (roleName: string) source =
        if roleName.Trim().ToLowerInvariant() = "manager" then
            reviewCalls source
        else
            source

    let private rethrowHost =
        """if (result && result.ok === false) {
      const err = new Error(result.reason || result.code);
      err.__jsFailure = { code: result.code, reason: result.reason || result.code };
      throw err;
    }"""

    /// The name a dispatch member derives when the payload carries only a
    /// charge: a slug of the charge, or "child" when the charge yields no
    /// slug. One rule, shared by fork and commission — the two members whose
    /// payload shape is `{ charge, name?, ... }` — so the same charge always
    /// names the same road. Resume is deliberately absent: its payload must
    /// carry the name of an existing road, and a missing name is refused by
    /// the host with `name-required`.
    let private deriveNameFromCharge =
        """    if (!spec.name) {
      const slug = String(spec.charge || "").toLowerCase().replace(/[^a-z0-9]+/g, "-").replace(/^-+|-+$/g, "").slice(0, 24);
      spec.name = slug || "child";
    }
"""

    /// The refusal emitted when the namespace cannot serve a member. The
    /// review namespace exists only where the review gate installed it; a
    /// surface without it refuses the read as data instead of reading an
    /// absent api.
    let private missingMemberGuard (memberName: string) =
        $"""    if (!this._api || typeof this._api.{memberName} !== "function") {{
      return {{
        ok: false,
        code: "REVIEW_ONLY",
        reason: "review-scoped reads are not available in this execution path"
      }};
    }}
"""

    /// The file members of a base class. `namespace` is the api expression
    /// the member reads through; `guardOf` builds the refusal that precedes
    /// the read when the namespace cannot serve the member.
    let private runtimeFileMethods
        (capabilities: Set<JsCapability>)
        (memberNamespace: string)
        (guardOf: string -> string)
        =
        [ if present capabilities "cat" then
              fill
                  """  async cat(path) {
{{guard}}    const result = await {{namespace}}.cat(path);
    {{rethrow_host}}
    return result.text;
  }"""
                  [ "rethrow_host", rethrowHost
                    "namespace", memberNamespace
                    "guard", guardOf "cat" ]

              fill
                  """  async stat(path) {
{{guard}}    const result = await {{namespace}}.stat(path);
    {{rethrow_host}}
    return result.stat;
  }"""
                  [ "rethrow_host", rethrowHost
                    "namespace", memberNamespace
                    "guard", guardOf "stat" ]
          if present capabilities "ls" then
              fill
                  """  async ls(path) {
{{guard}}    const result = await {{namespace}}.ls(path);
    {{rethrow_host}}
    return result.entries;
  }"""
                  [ "rethrow_host", rethrowHost
                    "namespace", memberNamespace
                    "guard", guardOf "ls" ]
          if present capabilities "rg" then
              fill
                  """  async rg(argv) {
{{guard}}    const result = await {{namespace}}.rg(argv);
    {{rethrow_host}}
    return result.paths !== undefined
      ? { exitCode: result.exitCode, paths: result.paths }
      : { exitCode: result.exitCode, messages: result.messages };
  }"""
                  [ "rethrow_host", rethrowHost
                    "namespace", memberNamespace
                    "guard", guardOf "rg" ] ]

    let private runtimeBaseClassCore reviewOnly (prose: Prose) (capabilities: Set<JsCapability>) : string =
        let methods =
            [ if reviewOnly then
                  "  constructor(api) { this._api = api; this.reviewOnly = new ReviewOnly(api.reviewOnly); }"
              else
                  "  constructor(api) { this._api = api; }"
              if not reviewOnly then
                  yield! runtimeFileMethods capabilities "this._api" (fun _ -> "")
              if present capabilities "cat" then
                  fill
                      """  whereAll(content, needle, from = 0) {
    const result = this._api.whereAll(content, needle, from);
    {{rethrow_host}}
    return result.spans;
  }"""
                      [ "rethrow_host", rethrowHost ]

                  fill
                      """  async where(content, needle, from = 0) {
    const stack = new Error().stack;
    const result = await this._api.where(content, needle, from, stack);
    {{rethrow_host}}
    return result && result.span !== undefined ? result.span : result;
  }"""
                      [ "rethrow_host", rethrowHost ]
              if present capabilities "write" then
                  fill
                      """  write(path, content) {
    const result = this._api.write(path, content);
    {{rethrow_host}}
  }"""
                      [ "rethrow_host", rethrowHost ]

                  fill
                      """  mkdir(path) {
    const result = this._api.mkdir(path);
    {{rethrow_host}}
    return { ok: true, path };
  }"""
                      [ "rethrow_host", rethrowHost ]
              if present capabilities "rm" then
                  fill
                      """  rm(path) {
    const result = this._api.rm(path);
    {{rethrow_host}}
    return { removed: result.removed };
  }"""
                      [ "rethrow_host", rethrowHost ]
              if present capabilities "mv" then
                  fill
                      """  mv(from, to) {
    const result = this._api.mv(from, to);
    {{rethrow_host}}
    return { moved: result.moved, destination: result.destination };
  }"""
                      [ "rethrow_host", rethrowHost ]
              if present capabilities "call" then
                  fill
                      """  async call(command) {
    if (arguments.length !== 1) throw Object.assign(new Error("call requires one command; intent belongs to the tool request."), { code: "INVALID_ARGUMENT" });
    return await this._api.call(command);
  }"""
                      []
              if has capabilities JsCapability.Exec then
                  """  async signal(name, signal) {
    if (arguments.length !== 2) throw Object.assign(new Error("signal requires name and signal."), { code: "INVALID_ARGUMENT" });
    return await this._api.signal(name, signal);
  }"""

                  """  async send(name, input) {
    if (arguments.length !== 2) throw Object.assign(new Error("send requires name and input."), { code: "INVALID_ARGUMENT" });
    return await this._api.send(name, input);
  }"""
              if present capabilities "ask" then
                  fill
                      """  async ask(question) {
    const stack = new Error().stack;
    const result = await this._api.ask(question, stack);
    {{rethrow_host}}
    return result;
  }"""
                      [ "rethrow_host", rethrowHost ]
              if present capabilities "commission" then
                  fill
                      """  async commission(payload) {
    const spec = typeof payload === "string" ? { charge: payload } : Object.assign({}, payload);
{{name_derivation}}
    return await this._api.commission(spec);
  }"""
                      [ "name_derivation", deriveNameFromCharge ]
              if present capabilities "fork" then
                  fill
                      """  async fork(payload) {
    const spec = typeof payload === "string" ? { charge: payload } : Object.assign({}, payload);
{{name_derivation}}
    return await this._api.fork(spec);
  }"""
                      [ "name_derivation", deriveNameFromCharge ]
              if present capabilities "resume" then
                  fill
                      """  async resume(payload) {
    const spec = typeof payload === "string" ? { charge: payload } : Object.assign({}, payload);
    return await this._api.resume(spec);
  }"""
                      []
              if present capabilities "devops" then
                  fill
                      """  async devops(intent) {
    return await this._api.devops(intent);
  }"""
                      []
              if present capabilities "horizon" then
                  fill
                      """  async horizon() {
    return await this._api.horizon();
  }"""
                      []
              if present capabilities "review" then
                  fill
                      """  async review(findings, note) {
    return await this._api.review(findings, note);
  }"""
                      []
              if present capabilities "fission" then
                  fill
                      """  async fission(payload) {
    return await this._api.fission(payload);
  }"""
                      []
              // resume/review/fetch/devops before park.
              // park is a lifecycle capability every js-* surface carries; it
              // is not a filesystem permission projection, so it is always
              // present in the runtime base class.
              fill
                  """  async park(payload) {
    return await this._api.park(payload);
  }
  async unpark(name, value) {
    if (arguments.length < 1 || arguments.length > 2) throw Object.assign(new Error("unpark requires name and optional value."), { code: "INVALID_ARGUMENT" });
    return await this._api.unpark(name, value);
  }
  async abandon(name) {
    return await this._api.abandon(name);
  }"""
                  []
              runStub prose ] in

        let reviewClass =
            if reviewOnly then
                String.concat
                    "\n\n"
                    ([ "class ReviewOnly {"; "  constructor(api) { this._api = api; }" ]
                     // The constructor already receives the review namespace
                     // itself (api.reviewOnly), so the members read through
                     // it directly. The guard checks the same expression it
                     // guards: a namespace that cannot serve the member
                     // refuses with the REVIEW_ONLY envelope.
                     @ runtimeFileMethods capabilities "this._api" missingMemberGuard
                     @ [ "}" ])
                + "\n\n"
            else
                "" in

        reviewClass + String.concat "\n\n" (classOpen :: methods @ [ "}" ])

    let runtimeBaseClass prose capabilities =
        runtimeBaseClassCore false prose capabilities

    let runtimeBaseClassForRole (roleName: string) prose capabilities =
        runtimeBaseClassCore (roleName.Trim().ToLowerInvariant() = "manager") prose capabilities

    let contract (prose: Prose) (toolName: string) (capabilities: Set<JsCapability>) =
        let verbs =
            [ if has capabilities JsCapability.Read then
                  yield! [ prose.VerbRead; "cat"; "stat"; "where"; "whereAll" ]
              if has capabilities JsCapability.Glob then
                  yield "ls"
              if has capabilities JsCapability.Grep then
                  yield "rg"
              if has capabilities JsCapability.Write then
                  yield! [ prose.VerbCreate; "mkdir" ]
              if has capabilities JsCapability.Exec then
                  yield! [ "call"; "signal"; "send" ] ]
            |> String.concat ", "

        let parallelTemplate =
            if has capabilities JsCapability.Write then
                prose.ContractParallelEdits
            else
                prose.ContractParallelReads

        fill
            prose.Contract
            [ "toolName", toolName
              "verbs", verbs
              "parallelLine", fill parallelTemplate [ "toolName", toolName ] ]

    let rules (prose: Prose) (capabilities: Set<JsCapability>) : string =
        [ if has capabilities JsCapability.Read then
              prose.ReadRules
          if has capabilities JsCapability.Glob then
              prose.Recipes
          if has capabilities JsCapability.Write then
              prose.WriteRules
          if has capabilities JsCapability.Remove then
              prose.Rm
          if has capabilities JsCapability.Move then
              prose.Mv
          if has capabilities JsCapability.Exec then
              prose.RunRules
          if has capabilities JsCapability.Fork || has capabilities JsCapability.Commission then
              prose.DelegationRules
          if has capabilities JsCapability.Write then
              prose.MutationRules ]
        |> String.concat "\n\n"

    let ultraExample (prose: Prose) (roleName: string) (capabilities: Set<JsCapability>) : JsExample option =
        let candidate =
            match roleName.Trim().ToLowerInvariant() with
            | "engineer" when has capabilities JsCapability.Write ->
                Some(set [ JsCapability.Read; JsCapability.Write ], prose.UltraEngineering)
            | "engineer" -> Some(set [ JsCapability.Read ], prose.UltraInvestigation)
            | "devops" -> Some(set [ JsCapability.Read; JsCapability.Exec ], prose.UltraDevOps)
            | "plan" -> Some(set [ JsCapability.Read ], prose.UltraPlan)
            // Manager's admission set must list every capability the manager
            // role currently projects: the manager capability set is
            // { Advisor, Fork, Glob, Grep, Horizon, Join, Read, Resume, Review }, and
            // `Set.isSubset capabilities admission` fails (so the ultra
            // example disappears) as soon as the role gains a capability this
            // set does not mention. Resume was added to the manager role
            // alongside the delegation-interface migration, so it belongs
            // here too; adding the next manager capability means adding it to
            // this set as well.
            | "manager" when
                Set.isSubset
                    capabilities
                    (set
                        [ JsCapability.Read
                          JsCapability.Glob
                          JsCapability.Grep
                          JsCapability.Fork
                          JsCapability.Resume
                          JsCapability.Horizon
                          JsCapability.Review ])
                && not (Set.isEmpty capabilities)
                ->
                Some(set [ JsCapability.Read; JsCapability.Glob ], prose.UltraManagerInvestigation)
            | _ -> None

        candidate
        |> Option.bind (fun (requires, source) ->
            if Set.isSubset requires capabilities then
                Some
                    { Requires = requires
                      Source = exampleSourceForRole roleName source }
            else
                None)

    let private renderFor
        (prose: Prose)
        (roleName: string)
        (toolName: string)
        (capabilities: Set<JsCapability>)
        (baseClass: string)
        : string =
        let ultraBlock =
            match ultraExample prose roleName capabilities with
            | Some example -> prose.UltraFraming + "\n\n```js\n" + example.Source + "\n```"
            | None -> prose.UltraUnavailable

        let blocks =
            [ prose.Header
              if roleName.Trim().ToLowerInvariant() = "manager" then
                  prose.ReviewOnlyRules
              contract prose toolName capabilities
              // The Plan surface has rules the shared contract does not
              // carry (the draft path, the DevOps delegation shape, the
              // read-only-until-review rule). They belong to the Plan role
              // alone; every other role's description stays byte-identical.
              if roleName.Trim().ToLowerInvariant() = "plan" then
                  prose.JsPlan
              "```js\n" + baseClass + "\n```"
              (if roleName.Trim().ToLowerInvariant() = "manager" then
                   reviewCalls (rules prose capabilities)
               else
                   rules prose capabilities)
              prose.Park
              prose.Deliver
              prose.MechanicalSemantic
              ultraBlock
              prose.Footer ]

        String.concat "\n\n" blocks

    let render (prose: Prose) (roleName: string) (toolName: string) (capabilities: Set<JsCapability>) : string =
        renderFor prose roleName toolName capabilities (publicBaseClassForRole prose roleName capabilities)
