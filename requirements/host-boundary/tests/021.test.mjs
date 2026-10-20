import test from 'node:test'

{
const { default: assert } = await import("node:assert/strict");
const { default: test } = await import("node:test");
const { readFileSync } = await import("node:fs");
const { join } = await import("node:path");
const { fileURLToPath } = await import("node:url");

const ROOT = fileURLToPath(new URL('../../..', import.meta.url))
const read = (path) => readFileSync(join(ROOT, path), 'utf8')

test('WHAT[host-boundary-021] HostSignalBootstrap is strictly a wiring composition root with 0 foreign internal imports', () => {
  const bootstrapSource = read('src/Wanxiangshu/OpenCode/Host/HostSignalBootstrap.fs')

  // Prohibit forbidden foreign internal domain opens (AGENTS.md Chapter 26)
  const forbiddenOpens = [
    /open\s+Wanxiangshu\.Mission\.Review\.Judgement\b/,
    /open\s+Wanxiangshu\.Mission\.Obligation\.Todo\b/,
    /open\s+Wanxiangshu\.Mission\.Finality\b/,
    /open\s+Wanxiangshu\.Mission\.Manager\.Life\b/,
    /open\s+Wanxiangshu\.Strength\.Prediction\b/,
    /open\s+Wanxiangshu\.Strength\.Replica\b/,
    /open\s+Wanxiangshu\.Enforcer\.Guidance\b/,
    /open\s+Wanxiangshu\.Enforcer\.Cycle\b/,
    /open\s+Wanxiangshu\.Context\.Trace\b/,
  ]

  for (const pattern of forbiddenOpens) {
    assert.doesNotMatch(
      bootstrapSource,
      pattern,
      `HostSignalBootstrap must not import foreign internal domain namespace: ${pattern}`,
    )
  }

  // Verify pure 5 responsibilities: construct, subscribe, route typed signal,
  // register the Host-owned reconcile drain, and register subscription disposal.
  assert.match(bootstrapSource, /module\s+HostSignalBootstrap\b/)
  assert.match(bootstrapSource, /type\s+WiredSignals\b/)
  assert.match(bootstrapSource, /let\s+wire\b/)
  assert.match(bootstrapSource, /HostSignalSubscribe\.trySubscribe/)
  assert.match(bootstrapSource, /do scope\.TrackReconcileShutdown\(fun \(\) -> reconciler\.StopAndDrain\(\)\)/)
  assert.match(bootstrapSource, /do scope\.TrackSubscription subscription/)

  // Prohibit foreign domain policy decisions or implicit workflow PC
  assert.doesNotMatch(bootstrapSource, /\bdecideModelPolicy\b/)
  assert.doesNotMatch(bootstrapSource, /\bdecideRecoverySemantics\b/)
  assert.doesNotMatch(bootstrapSource, /\bdecideFissionPolicy\b/)
  assert.doesNotMatch(bootstrapSource, /\bdecideFinality\b/)
  assert.doesNotMatch(bootstrapSource, /\bdecideAssistanceSuccessor\b/)
  assert.doesNotMatch(bootstrapSource, /\bCurrentStage\b/)
  assert.doesNotMatch(bootstrapSource, /\bNextStep\b/)
  assert.doesNotMatch(bootstrapSource, /\bResumeAt\b/)
})
test('WHAT[host-boundary-021] HostSignalBootstrap delegates policy to published owner contracts', () => {
  const bootstrapSource = read('src/Wanxiangshu/OpenCode/Host/HostSignalBootstrap.fs')

  // Host observations are persisted through the exact execution-binding owner;
  // provider-step lifecycle remains behind ModelRouting's published contract.
  assert.match(bootstrapSource, /ProviderLifecycle\.persistProviderStartedFromObservation/)
  assert.match(bootstrapSource, /ModelRouting\.endProviderStep/)

  // Managed chat admission is one transaction. The composition root decodes
  // once, resolves through PromptIngress, and supplies only ModelRouting's Host
  // projection port to the transaction owner.
  assert.match(bootstrapSource, /PromptIngress\.resolveDecision/)
  assert.match(bootstrapSource, /ChatAdmissionTransaction\.production/)
  assert.match(bootstrapSource, /ChatAdmissionTransaction\.execute/)
  assert.match(bootstrapSource, /createTransaction \(ModelRouting\.projectHostModel output\)/)
  assert.doesNotMatch(bootstrapSource, /PromptIngress\.create(?:Decision)?Hook/)
  assert.doesNotMatch(bootstrapSource, /ModelRouting\.routeChatExecution/)
  assert.doesNotMatch(bootstrapSource, /ModelRouting\.projectRoutedModel/)
  assert.doesNotMatch(bootstrapSource, /ModelRouting\.releasePhysicalExecution/)

  // Fission policy delegated to FissionHost owner
  assert.match(bootstrapSource, /FissionHost\.routeAttemptAborted/)
  assert.match(bootstrapSource, /FissionHost\.observePhysicalExecutionEnd/)

  // Session deletion delegated to HostSessionDeletion owner
  assert.match(bootstrapSource, /HostSessionDeletion\.handle/)
})
}

{
const { default: assert } = await import("node:assert/strict");
const { default: test } = await import("node:test");
const { readFileSync } = await import("node:fs");
const { join } = await import("node:path");
const { fileURLToPath } = await import("node:url");

const ROOT = fileURLToPath(new URL('../../..', import.meta.url))
const read = (path) => readFileSync(join(ROOT, path), 'utf8')

test('WHAT[host-boundary-021] HOST_021_plugin_load_graph_has_no_semantic_recovery_or_workspace_mutation', () => {
  const boot = read('src/Wanxiangshu/OpenCode/Plugin/PluginBoot.fs')
  const signal = read('src/Wanxiangshu/OpenCode/Host/HostSignalBootstrap.fs')
  const workspaceStore = read('src/Wanxiangshu/OpenCode/Host/WorkspaceEventStore.fs')
  const spike = read('src/Wanxiangshu/OpenCode/Plugin/SpikePlugin.fs')
  const recoveryWiring = read('src/Wanxiangshu/OpenCode/Plugin/PluginRecoveryWiring.fs')

  assert.doesNotMatch(boot, /restoreSessionParents/)
  assert.doesNotMatch(boot, /HookDispatcher\.ensure/)
  assert.doesNotMatch(boot, /PluginRecoveryWiring\.attach/)

  assert.doesNotMatch(signal, /\.Recover\(\)/)
  assert.doesNotMatch(signal, /FissionHost\.recoverGroups/)
  assert.doesNotMatch(workspaceStore, /JsToolsTransactionStore\.recoverCurrent/)

  // PluginRecoveryWiring.attach is registration, not recovery execution: load
  // installs one durability-activation callback and the callback owns the later
  // background lifecycle notifications. SpikePlugin must not invoke those
  // lifecycle events directly.
  assert.match(spike, /PluginRecoveryWiring\.attach boot/)
  assert.doesNotMatch(spike, /SignalChatRecovery|PluginRuntimeReloaded|CapacityProjectionReplayed/)
  assert.match(
    recoveryWiring,
    /scope\.AttachDurabilityActivation\(fun \(\) ->\s*scope\.RunBackground\(fun \(\) ->/,
  )
  assert.equal([...recoveryWiring.matchAll(/AttachDurabilityActivation/g)].length, 1)
  assert.doesNotMatch(recoveryWiring, /restoreLinkedChildren|recoverFamilyDirect|defaultRecoverPromptClaims/)
})
test('WHAT[host-boundary-021] HOST_021_broken_tool_recovery_APIs_do_not_exist', () => {
  const fission = read('src/Wanxiangshu/Execution/Fission/OpenCode/Host.fs')
  const jsStore = read('src/Wanxiangshu/Repository/Programming/Js/TransactionStore.fs')

  assert.doesNotMatch(fission, /let\s+recoverGroups\b/)
  assert.doesNotMatch(jsStore, /let\s+recoverCurrent\b/)
})
test('WHAT[host-boundary-021] HOST_021_ordinary_join_does_not_reenlist_old_durable_tool_state', () => {
  const join = read('src/Wanxiangshu/Execution/Delegation/Fork/Host/Join.fs')
  const joinTool = read('src/Wanxiangshu/Execution/Delegation/Fork/OpenCode/JoinTool.fs')

  assert.match(join, /currentProcessHandle/)
  assert.match(join, /drainFromJournalWhere/)
  assert.doesNotMatch(joinTool, /tryMembershipOfLane/)
})
test('WHAT[host-boundary-021] HOST_021_plugin_load_does_not_append_RuntimeStarted', () => {
  const boot = read('src/Wanxiangshu/OpenCode/Plugin/PluginBoot.fs')
  const journalWriter = read('src/Wanxiangshu/Persistence/Journal/EventStoreJournalWriter.fs')

  assert.doesNotMatch(boot, /RuntimeStarted/)
  assert.doesNotMatch(journalWriter, /resumeOrCreate[\s\S]{0,1800}appendInitial[\s\S]{0,600}RuntimeStarted/)
})
}

{
const { default: assert } = await import("node:assert/strict");
const { default: test } = await import("node:test");
const XWireSurface = await import("../../../dist/Context/Prefix/XWireSurface.js");

const baseProjection = {
  messages: [
    { role: 'user', parts: [{ kind: 'text', text: 'hello' }] },
    { role: 'assistant', parts: [{ kind: 'text', text: 'answer' }] },
  ],
}
const acceptedRetryInput = (overrides = {}) => ({
  journal: true,
  sessionId: 'ses_x',
  acceptedRetry: true,
  physicalUser: 'user-1',
  acceptedPhysicalUser: 'user-1',
  currentProjection: baseProjection,
  ...overrides,
})

test('WHAT[host-boundary-021] XWIRE_covered_prefix_digest_is_sha256', () => {
  assert.equal(
    XWireSurface.coveredPrefixDigest(baseProjection, 1),
    '823d6b40827ef755cd32aeef72b073a7883c01dcb29c5fdf3318c237a59f1129',
  )
})
test('WHAT[host-boundary-021] XWIRE_no_journal_is_a_noop', () => {
  const result = XWireSurface.transform(acceptedRetryInput({ journal: false }))
  assert.equal(result.ok, true)
  assert.equal(result.noop, true)
  assert.equal(result.changed, false)
  assert.equal(result.consumed, false)
})
test('WHAT[host-boundary-021] XWIRE_no_session_id_in_output_is_a_noop', () => {
  const result = XWireSurface.transform(acceptedRetryInput({ sessionId: '' }))
  assert.equal(result.ok, true)
  assert.equal(result.noop, true)
  assert.equal(result.changed, false)
  assert.equal(result.consumed, false)
})
test('WHAT[host-boundary-021] XWIRE_unaccepted_retry_is_a_noop', () => {
  const result = XWireSurface.transform(acceptedRetryInput({ acceptedRetry: false }))
  assert.equal(result.ok, true)
  assert.equal(result.noop, true)
  assert.equal(result.changed, false)
  assert.equal(result.consumed, false)
})
test('WHAT[host-boundary-021] XWIRE_exact_accepted_retry_consumes_plan_without_message_rewrite', () => {
  const result = XWireSurface.transform(acceptedRetryInput())
  assert.equal(result.ok, true)
  assert.equal(result.noop, false)
  assert.equal(result.changed, false)
  assert.equal(result.consumed, true)
  assert.equal(result.error, null)
  assert.equal(result.output, baseProjection)
})
test('WHAT[host-boundary-021] XWIRE_reconcile_completed_clears_plan', () => {
  const result = XWireSurface.reconcile({ hasPlan: true, outcome: 'completed' })
  assert.equal(result.cleared, true)
  assert.equal(result.keptPlan, false)
})
test('WHAT[host-boundary-021] XWIRE_reconcile_failed_clears_plan', () => {
  const result = XWireSurface.reconcile({ hasPlan: true, outcome: 'failed' })
  assert.equal(result.cleared, true)
  assert.equal(result.keptPlan, false)
})
test('WHAT[host-boundary-021] XWIRE_reconcile_aborted_clears_plan', () => {
  const result = XWireSurface.reconcile({ hasPlan: true, outcome: 'aborted' })
  assert.equal(result.cleared, true)
  assert.equal(result.keptPlan, false)
})
test('WHAT[host-boundary-021] XWIRE_reconcile_unknown_reread_keeps_the_plan', () => {
  const result = XWireSurface.reconcile({ hasPlan: true, outcome: 'in-progress' })
  assert.equal(result.cleared, false)
  assert.equal(result.keptPlan, true)
})
test('WHAT[host-boundary-021] XWIRE_reconcile_no_plan_is_inert', () => {
  const result = XWireSurface.reconcile({ hasPlan: false, outcome: 'completed' })
  assert.equal(result.cleared, false)
  assert.equal(result.keptPlan, false)
})
}
