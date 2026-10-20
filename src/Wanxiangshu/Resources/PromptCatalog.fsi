namespace Wanxiangshu.Resources

/// Canonical provider system composition:
/// Common Law → Role Law → inherited Office Library.
type PromptCatalog =
    { ManagerSystemPrompt: string
      EngineerSystemPrompt: string
      DevopsSystemPrompt: string
      OrchestratorSystemPrompt: string

      PlanSystemPrompt: string
      AdvisorSystemPrompt: string }
