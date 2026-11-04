#load "../../../../src/Wanxiangshu/Interaction/Authority/Origin.fs"
#load "../../../../src/Wanxiangshu/Interaction/Authority/LifeClassification.fs"

open Wanxiangshu.Interaction.Authority

let check kind manager life expected =
    let actual = AuthorityLifeClassification.closureKindForRoot kind manager life
    if actual <> expected then failwithf "Expected %A, got %A" expected actual

check PromptRootAuthorityKind.HumanRoot true None ("HumanRootManagerLife", None)
check PromptRootAuthorityKind.HumanRoot false None ("HumanRootManagedRun", None)
check PromptRootAuthorityKind.AgentOwnerRoot false (Some "Attached") ("AgentOwnerAttachedWork", Some "Attached")
check PromptRootAuthorityKind.AgentOwnerRoot false (Some "InternalLeaf") ("AgentOwnerInternalLeaf", Some "InternalLeaf")
check PromptRootAuthorityKind.AgentOwnerRoot false (Some "Child") ("AgentOwnerChildWork", Some "Child")
check PromptRootAuthorityKind.AgentOwnerRoot false None ("AgentOwnerChildWork", Some "Child")
check PromptRootAuthorityKind.AgentOwnerRoot false (Some "bogus") ("AgentOwnerChildWork", Some "Child")
