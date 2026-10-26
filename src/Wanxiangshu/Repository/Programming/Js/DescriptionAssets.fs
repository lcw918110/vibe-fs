namespace Wanxiangshu.Repository.Programming.Js

open Wanxiangshu.Participant.Provider

/// PROMPT-019: load already-localized js-program prose. Domain assembles;
/// this module binds language.
module JsDescriptionAssets =

    let private text (lang: ProviderLanguage) (path: string) =
        ProviderProse.render lang path Map.empty

    let private template (lang: ProviderLanguage) (path: string) = ProviderResources.readText lang path

    let load (lang: ProviderLanguage) : JsCanonicalDescription.Prose =
        { Header = text lang JsCanonicalDescription.Path.Header

          Footer = text lang JsCanonicalDescription.Path.Footer
          Contract = template lang JsCanonicalDescription.Path.Contract

          ContractParallelEdits = template lang JsCanonicalDescription.Path.ContractParallelEdits
          ContractParallelReads = template lang JsCanonicalDescription.Path.ContractParallelReads
          VerbRead = text lang JsCanonicalDescription.Path.VerbRead

          VerbCreate = text lang JsCanonicalDescription.Path.VerbCreate
          ReadRules = template lang JsCanonicalDescription.Path.ReadRules
          ReviewOnlyRules = text lang JsCanonicalDescription.Path.ReviewOnlyRules
          Recipes = text lang JsCanonicalDescription.Path.Recipes

          WriteRules = text lang JsCanonicalDescription.Path.WriteRules
          MutationRules = text lang JsCanonicalDescription.Path.MutationRules
          Rm = text lang JsCanonicalDescription.Path.Rm
          Mv = text lang JsCanonicalDescription.Path.Mv
          RunRules = text lang JsCanonicalDescription.Path.RunRules
          Deliver = ""
          DelegationRules = text lang JsCanonicalDescription.Path.DelegationRules
          UltraFraming = text lang JsCanonicalDescription.Path.UltraFraming
          UltraUnavailable = text lang JsCanonicalDescription.Path.UltraUnavailable
          UltraEngineering = text lang JsCanonicalDescription.Path.UltraEngineering
          UltraInvestigation = text lang JsCanonicalDescription.Path.UltraInvestigation
          UltraDevOps = text lang JsCanonicalDescription.Path.UltraDevOps
          UltraManagerInvestigation = text lang JsCanonicalDescription.Path.UltraManagerInvestigation
          UltraPlan = text lang JsCanonicalDescription.Path.UltraPlan
          MechanicalSemantic = text lang JsCanonicalDescription.Path.MechanicalSemantic

          ReasonRunUnimplemented = text lang JsCanonicalDescription.Path.ReasonRunUnimplemented

          Park = template lang JsCanonicalDescription.Path.Park
          ParkedPayload = template lang JsCanonicalDescription.Path.ParkedPayload
          BaseClass = template lang JsCanonicalDescription.Path.BaseClass
          BaseClassRead = template lang JsCanonicalDescription.Path.BaseClassRead
          BaseClassAnchors = template lang JsCanonicalDescription.Path.BaseClassAnchors
          BaseClassGlob = template lang JsCanonicalDescription.Path.BaseClassGlob
          BaseClassGrep = template lang JsCanonicalDescription.Path.BaseClassGrep
          BaseClassWrite = template lang JsCanonicalDescription.Path.BaseClassWrite
          BaseClassRemove = template lang JsCanonicalDescription.Path.BaseClassRemove
          BaseClassMove = template lang JsCanonicalDescription.Path.BaseClassMove
          BaseClassExec = template lang JsCanonicalDescription.Path.BaseClassExec
          BaseClassFork = template lang JsCanonicalDescription.Path.BaseClassFork
          BaseClassResume = template lang JsCanonicalDescription.Path.BaseClassResume
          BaseClassCommission = template lang JsCanonicalDescription.Path.BaseClassCommission
          BaseClassDevOps = template lang JsCanonicalDescription.Path.BaseClassDevOps
          BaseClassFission = template lang JsCanonicalDescription.Path.BaseClassFission
          BaseClassHorizon = template lang JsCanonicalDescription.Path.BaseClassHorizon
          BaseClassAsk = template lang JsCanonicalDescription.Path.BaseClassAsk
          BaseClassReview = template lang JsCanonicalDescription.Path.BaseClassReview
          BaseClassLifecycle = template lang JsCanonicalDescription.Path.BaseClassLifecycle
          JsPlan = text lang JsCanonicalDescription.Path.JsPlan }

    let argProgram (lang: ProviderLanguage) =
        text lang JsCanonicalDescription.Path.ArgProgram

    let missingProgram (lang: ProviderLanguage) =
        text lang JsCanonicalDescription.Path.MissingProgram

    let viewDeclarationExtra (lang: ProviderLanguage) =
        text lang JsCanonicalDescription.Path.ViewDeclarationExtra
