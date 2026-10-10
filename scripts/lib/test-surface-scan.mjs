// Test-surface debt scanner (P1/P2 shared core).
//
// Scans the complete requirements/**/tests executable dependency zone (.mjs
// and .js), including support, fixtures, helpers, e2e, and integration files.
// It reports the debt
// classes a semantic test must not carry (TASK.md §1):
//
//   A. deep production import   import '...dist/<internal>.js' (not fable_modules)
//   B. Fable export discovery   Object.keys / Object.entries on F# modules and
//                              startsWith('Foo__') / endsWith('_Bar') lookup
//   C. Fable representation     .tag / .fields / .cases() / FSharp* names / fable_modules
//   D. legacy interop authority member( / bind( / fableInstanceMethod( / prod( /
//                              toList( / caseOf( / payloadOf( / resultOf( / unwrapOption(
//
// Compiler/build verification files (emitted-surface pins, domain.meta's artifact self-contract,
// distribution artifact tests, the representation validator, and the coverage runner) are exempt
// only by explicit path allowlist: their subject is the compiled/representation artifact, not
// product semantics. Product-package tests/support, fixtures, e2e, integration, and contract
// adapters remain in the scanned zone.

import { existsSync, readFileSync } from 'node:fs'
import { dirname, join, relative, resolve } from 'node:path'
import { fileURLToPath } from 'node:url'
import { parse } from 'acorn'
import { walk } from './walk.mjs'

export const REQUIREMENTS_ROOT = join(
  join(join(join(fileURLToPath(import.meta.url), '..'), '..'), '..'),
  'requirements',
)

/** Compiler/build verification: subject is the emitted artifact, not semantics. */
export const BUILD_VERIFICATION_FILES = new Set([
  'requirements/verification-system/tests/011.test.mjs',
  'requirements/distribution/tests/001.test.mjs',
  'requirements/distribution/tests/002.test.mjs',
  'requirements/distribution/tests/005.test.mjs',
  'requirements/distribution/tests/006.test.mjs',
  'requirements/distribution/tests/007.test.mjs',
  // Representation validator: its subject is the JS-native boundary rules,
  // so it must be able to spell out the forbidden Fable shapes.
  'requirements/verification-system/tests/support/js-contract.mjs',
  'requirements/verification-system/tests/support/run-inner.mjs',
  'requirements/verification-system/tests/support/coverage-policy.mjs',
])

/** Physical host-shape canaries (HOST-BOUNDARY-019): their subject is the raw
 *  Host SDK snapshot / Fable representation itself — locating a ToolPart,
 *  proving run-id equivalence, or observing a projection requires reading the
 *  exact raw shape. Routing these through a semantic surface would hide
 *  precisely what the canary exists to prove. */
export const HOST_PHYSICAL_CANARY_FILES = new Set([
  'requirements/host-boundary/tests/006.test.mjs',
  'requirements/host-boundary/tests/009.test.mjs',
  'requirements/host-boundary/tests/010.test.mjs',
  'requirements/host-boundary/tests/011.test.mjs',
  'requirements/host-boundary/tests/012.test.mjs',
  'requirements/host-boundary/tests/host-message-projection.test.mjs',
  'requirements/host-boundary/tests/host-session-context.test.mjs',
  'requirements/host-boundary/tests/host010-run-id-equivalence.test.mjs',
  'requirements/host-boundary/tests/magic-todo-host-canaries.test.mjs',
  'requirements/host-boundary/tests/session-snapshot-locality.test.mjs',
  // Physical-exit canary: the child must reach the real Diagnostic.fatal and
  // FatalProcess.trip links — a wire-level invariant, not a test seam.
  'requirements/host-boundary/tests/fixtures/fatal-process-child.fixture.mjs',
  // Plan relay host canary: continuous three-tenure relay in a single physical session
  'requirements/planning/tests/host-canary-plan-relay.test.mjs',
])

/**
 * Registered semantic-surface manifest (JS-SEMANTIC-SURFACE-002/003).
 *
 * A semantic test may import a registered surface directly: the surface IS the
 * legal entry point (owner boundary translation, JSON-shaped in/out). Deep
 * imports of any other dist module remain debt. Register here when a surface
 * is established — registration requires the full manifest below (owner
 * package, governing laws, production source, representation, kind), so a
 * surface exists because a semantic component owns a contract, never because
 * a test wants access (TASK.md §9/§10, PR 4).
 */
export const SURFACE_MANIFEST = [
  {
    module: 'Interaction/Authority/Surface.js',
    owner: 'participant-identity',
    laws: ['PID-005', 'PID-002'],
    source: 'src/Wanxiangshu/Interaction/Authority/Surface.fs',
    representation: 'json',
    kind: 'pure',
  },
  {
    module: 'OpenCode/Host/ProviderAttemptStopFenceSurface.js',
    owner: 'provider-attempt-recovery',
    laws: ['PAR-022'],
    source: 'src/Wanxiangshu/OpenCode/Host/ProviderAttemptStopFenceSurface.fs',
    representation: 'opaque-capability',
    kind: 'resource',
  },
  {
    module: 'Participant/Persona/Surface.js',
    owner: 'participant-identity',
    laws: ['PID-001', 'PID-002', 'PID-003', 'PID-007', 'PID-009', 'PID-010'],
    source: 'src/Wanxiangshu/Participant/Persona/Surface.fs',
    representation: 'json',
    kind: 'pure',
  },
  {
    module: 'OpenCode/Plugin/Plugin.js',
    owner: 'execution-model-routing',
    laws: ['EMR-003', 'EMR-009'],
    source: 'src/Wanxiangshu/OpenCode/Plugin/Plugin.fs',
    representation: 'opaque-capability',
    kind: 'resource',
  },
  {
    module: 'OpenCode/Host/BloggerChronicleSurface.js',
    owner: 'cognitive-environment',
    laws: ['COGNITIVE-ENVIRONMENT-015'],
    source: 'src/Wanxiangshu/OpenCode/Host/BloggerChronicleSurface.fs',
    representation: 'opaque-capability',
    kind: 'resource',
  },
  {
    module: 'OpenCode/Host/PluginTransformSurface.js',
    owner: 'host-boundary',
    laws: ['HOST-BOUNDARY-019'],
    source: 'src/Wanxiangshu/OpenCode/Host/PluginTransformSurface.fs',
    representation: 'json',
    kind: 'pure',
  },
  {
    module: 'OpenCode/Host/LoadRecoverySurface.js',
    owner: 'crash-reconciliation',
    laws: ['CRASH-RECONCILIATION-018', 'CRASH-RECONCILIATION-020', 'CRASH-RECONCILIATION-021'],
    source: 'src/Wanxiangshu/OpenCode/Host/LoadRecoverySurface.fs',
    representation: 'opaque-capability',
    kind: 'resource',
  },
  {
    module: 'OpenCode/Host/ChatAdmission/IntentSurface.js',
    owner: 'interaction-authority',
    laws: ['INTERACTION-AUTHORITY-005', 'INTERACTION-AUTHORITY-007', 'INTERACTION-AUTHORITY-008', 'INTERACTION-AUTHORITY-009'],
    source: 'src/Wanxiangshu/OpenCode/Host/ChatAdmission/IntentSurface.fs',
    representation: 'json',
    kind: 'pure',
  },
  {
    module: 'OpenCode/Host/ChatAdmission/TransactionSurface.js',
    owner: 'managed-chat-execution',
    laws: ['CHATEXEC-003', 'CHATEXEC-007', 'CHATEXEC-012'],
    source: 'src/Wanxiangshu/OpenCode/Host/ChatAdmission/TransactionSurface.fs',
    representation: 'json',
    kind: 'resource',
  },
  {
    module: 'OpenCode/Host/ModelRoutingSurface.js',
    owner: 'execution-model-routing',
    laws: [
      'EMR-001',
      'EMR-002',
      'EMR-003',
      'EMR-004',
      'EMR-005',
      'EMR-006',
      'EMR-007',
      'EMR-008',
      'EMR-009',
      'EMR-010',
      'EMR-011',
      'EMR-012',
      'EMR-013',
      'EMR-014',
      'EMR-017',
    ],
    source: 'src/Wanxiangshu/OpenCode/Host/ModelRoutingSurface.fs',
    representation: 'opaque-capability',
    kind: 'resource',
  },
  {
    module: 'OpenCode/Host/ReliabilityDiagnosticsSurface.js',
    owner: 'host-boundary',
    laws: ['HOST-BOUNDARY-025'],
    source: 'src/Wanxiangshu/OpenCode/Host/ReliabilityDiagnosticsSurface.fs',
    representation: 'json',
    kind: 'resource',
  },
  {
    module: 'OpenCode/Host/HookPolicySurface.js',
    owner: 'host-boundary',
    laws: ['HOST-BOUNDARY-024'],
    source: 'src/Wanxiangshu/OpenCode/Host/HookPolicySurface.fs',
    representation: 'json',
    kind: 'pure',
  },
  {
    module: 'Context/Companion/Blogger/TomlSurface.js',
    owner: 'provider-projection',
    laws: ['PROVIDER-PROJECTION-009'],
    source: 'src/Wanxiangshu/Context/Companion/Blogger/TomlSurface.fs',
    representation: 'json',
    kind: 'pure',
  },
  {
    module: 'Execution/Delegation/Fork/ChildRecoverySurface.js',
    owner: 'crash-reconciliation',
    laws: ['CRASH-009', 'CRASH-010', 'CRASH-011', 'CRASH-012'],
    source: 'src/Wanxiangshu/Execution/Delegation/Fork/ChildRecoverySurface.fs',
    representation: 'json',
    kind: 'pure',
  },
  {
    module: 'Execution/Delegation/Fork/OpenCode/JoinSurface.js',
    owner: 'delegation',
    laws: ['DELEG-013', 'DELEG-015'],
    source: 'src/Wanxiangshu/Execution/Delegation/Fork/OpenCode/JoinSurface.fs',
    representation: 'json',
    kind: 'pure',
  },
  {
    module: 'Execution/Delegation/Fork/CleanBreakSurface.js',
    owner: 'crash-reconciliation',
    laws: ['CRASH-009', 'CRASH-012', 'EFFECT-ACCOUNTING-007'],
    lawOwners: { 'EFFECT-ACCOUNTING-007': 'effect-accounting' },
    source: 'src/Wanxiangshu/Execution/Delegation/Fork/CleanBreakSurface.fs',
    representation: 'json',
    kind: 'pure',
  },
  {
    module: 'Execution/Session/Recovery/Surface.js',
    owner: 'crash-reconciliation',
    laws: ['CRASH-005', 'CRASH-010', 'CRASH-013', 'CRASH-014'],
    source: 'src/Wanxiangshu/Execution/Session/Recovery/Surface.fs',
    representation: 'json',
    kind: 'pure',
  },
  {
    module: 'Execution/Session/OpenCode/HorizonSurface.js',
    owner: 'delegation',
    laws: ['PARTICIPANT-HORIZON-004', 'PARTICIPANT-HORIZON-011'],
    lawOwners: {
      'PARTICIPANT-HORIZON-004': 'participant-horizon',
      'PARTICIPANT-HORIZON-011': 'participant-horizon',
    },
    source: 'src/Wanxiangshu/Execution/Session/OpenCode/HorizonSurface.fs',
    representation: 'json',
    kind: 'pure',
  },
  {
    module: 'Context/Companion/Blogger/Runtime/CycleSurface.js',
    owner: 'crash-reconciliation',
    laws: ['CRASH-016', 'EFFECT-ACCOUNTING-004', 'EFFECT-ACCOUNTING-008'],
    lawOwners: {
      'EFFECT-ACCOUNTING-004': 'effect-accounting',
      'EFFECT-ACCOUNTING-008': 'effect-accounting',
    },
    source: 'src/Wanxiangshu/Context/Companion/Blogger/Runtime/CycleSurface.fs',
    representation: 'json',
    kind: 'pure',
  },
  {
    module: 'Execution/Delegation/Fork/LifecycleSurface.js',
    owner: 'managed-session-lifecycle',
    laws: ['MANAGED-SESSION-012'],
    source: 'src/Wanxiangshu/Execution/Delegation/Fork/LifecycleSurface.fs',
    representation: 'json',
    kind: 'pure',
  },
  {
    module: 'Enforcer/RepairSurface.js',
    owner: 'provider-attempt-recovery',
    laws: ['PAR-012'],
    source: 'src/Wanxiangshu/Enforcer/RepairSurface.fs',
    representation: 'json',
    kind: 'pure',
  },
  {
    module: 'Participant/Provider/Attempt/Fallback/ProviderFailureSurface.js',
    owner: 'provider-attempt-recovery',
    laws: [
      'PAR-001',
      'PAR-002',
      'PAR-003',
      'PAR-004',
      'PAR-005',
      'PAR-006',
      'PAR-007',
      'PAR-008',
      'PAR-009',
      'PAR-010',
      'PAR-011',
      'PAR-013',
      'PAR-014',
      'PAR-016',
      'PAR-019',
      'PAR-020',
      'PAR-021',
      'EFFECT-ACCOUNTING-004',
      'VERIFICATION-SYSTEM-008',
    ],
    lawOwners: {
      'EFFECT-ACCOUNTING-004': 'effect-accounting',
      'VERIFICATION-SYSTEM-008': 'verification-system',
    },
    source: 'src/Wanxiangshu/Participant/Provider/Attempt/Fallback/ProviderFailureSurface.fs',
    representation: 'opaque-capability',
    kind: 'resource',
  },
  {
    module: 'Execution/Delegation/SyncDelegate/Surface.js',
    owner: 'delegation',
    laws: ['DELEG-005', 'DELEG-010', 'DELEG-015', 'DELEG-019', 'DELEG-021', 'DELEG-022', 'DELEG-025', 'DELEG-031', 'DISPATCH-PROTOCOL-003', 'DISPATCH-PROTOCOL-007', 'MANAGED-SESSION-001', 'MANAGED-SESSION-004', 'MANAGED-SESSION-009', 'MANAGED-SESSION-014'],
    lawOwners: {
      'DISPATCH-PROTOCOL-003': 'dispatch-protocol',
      'DISPATCH-PROTOCOL-007': 'dispatch-protocol',
      'MANAGED-SESSION-001': 'managed-session-lifecycle',
      'MANAGED-SESSION-004': 'managed-session-lifecycle',
      'MANAGED-SESSION-009': 'managed-session-lifecycle',
      'MANAGED-SESSION-014': 'managed-session-lifecycle',
    },
    source: 'src/Wanxiangshu/Execution/Delegation/SyncDelegate/Surface.fs',
    representation: 'opaque-capability',
    kind: 'resource',
  },
  {
    module: 'Execution/Delegation/DelegatedToolEstimateSurface.js',
    owner: 'delegation',
    laws: ['DELEG-022'],
    source: 'src/Wanxiangshu/Execution/Delegation/DelegatedToolEstimateSurface.fs',
    representation: 'json',
    kind: 'pure',
  },
  {
    module: 'Execution/Delegation/HandoffSurface.js',
    owner: 'delegation',
    laws: ['DELEG-024'],
    source: 'src/Wanxiangshu/Execution/Delegation/HandoffSurface.fs',
    representation: 'json',
    kind: 'pure',
  },
  {
    module: 'Execution/Delegation/Fork/Surface.js',
    owner: 'delegation',
    laws: ['DELEG-019'],
    source: 'src/Wanxiangshu/Execution/Delegation/Fork/Surface.fs',
    representation: 'json',
    kind: 'pure',
  },
  {
    module: 'Execution/Delegation/Fork/OpenCode/ToolSurface.js',
    owner: 'delegation',
    laws: ['DELEG-024', 'DELEG-025', 'DELEG-026'],
    source: 'src/Wanxiangshu/Execution/Delegation/Fork/OpenCode/ToolSurface.fs',
    representation: 'opaque-capability',
    kind: 'resource',
  },
  {
    module: 'Execution/Fission/Surface.js',
    owner: 'intra-participant-parallelism',
    laws: [
      'INTRA-PARTICIPANT-PARALLELISM-001',
      'INTRA-PARTICIPANT-PARALLELISM-002',
      'INTRA-PARTICIPANT-PARALLELISM-003',
      'INTRA-PARTICIPANT-PARALLELISM-004',
      'INTRA-PARTICIPANT-PARALLELISM-005',
      'INTRA-PARTICIPANT-PARALLELISM-006',
      'INTRA-PARTICIPANT-PARALLELISM-007',
      'INTRA-PARTICIPANT-PARALLELISM-008',
      'INTRA-PARTICIPANT-PARALLELISM-009',
      'INTRA-PARTICIPANT-PARALLELISM-011',
      'INTRA-PARTICIPANT-PARALLELISM-013',
      'INTRA-PARTICIPANT-PARALLELISM-014',
      'INTRA-PARTICIPANT-PARALLELISM-015',
    ],
    source: 'src/Wanxiangshu/Execution/Fission/Surface.fs',
    representation: 'json',
    kind: 'resource',
  },
  {
    module: 'OpenCode/Host/FissionHostSurface.js',
    owner: 'intra-participant-parallelism',
    laws: ['INTRA-PARTICIPANT-PARALLELISM-009'],
    source: 'src/Wanxiangshu/OpenCode/Host/FissionHostSurface.fs',
    representation: 'json',
    kind: 'pure',
  },
  {
    module: 'Foundation/RolesSurface.js',
    owner: 'capability-enforcement',
    laws: ['ENF-002', 'MANAGED-SESSION-023', 'MANAGED-SESSION-024'],
    lawOwners: {
      'MANAGED-SESSION-023': 'managed-session-lifecycle',
      'MANAGED-SESSION-024': 'managed-session-lifecycle',
    },
    source: 'src/Wanxiangshu/Foundation/RolesSurface.fs',
    representation: 'json',
    kind: 'pure',
  },
  {
    module: 'Foundation/SyntheticTomlSurface.js',
    owner: 'provider-projection',
    laws: ['PROVIDER-PROJECTION-008', 'PROVIDER-PROJECTION-010', 'PROVIDER-PROJECTION-012'],
    source: 'src/Wanxiangshu/Foundation/SyntheticTomlSurface.fs',
    representation: 'json',
    kind: 'pure',
  },
  {
    module: 'Host/Contract/ToolResultBound.js',
    owner: 'host-boundary',
    laws: ['HOST-BOUNDARY-015'],
    source: 'src/Wanxiangshu/Host/Contract/ToolResultBound.fs',
    representation: 'json',
    kind: 'pure',
  },
  {
    module: 'Host/Contract/CompactionPolicySurface.js',
    owner: 'context-compression',
    laws: ['CONTEXT-COMPRESSION-002', 'CONTEXT-COMPRESSION-005', 'HOST-BOUNDARY-007'],
    lawOwners: { 'HOST-BOUNDARY-007': 'host-boundary' },
    source: 'src/Wanxiangshu/Host/Contract/CompactionPolicySurface.fs',
    representation: 'json',
    kind: 'pure',
  },
  {
    module: 'OpenCode/Host/QuiescenceSurface.js',
    owner: 'capability-enforcement',
    laws: ['ENF-018', 'ENF-019', 'CRASH-001', 'CRASH-006', 'CRASH-008'],
    lawOwners: {
      'CRASH-001': 'crash-reconciliation',
      'CRASH-006': 'crash-reconciliation',
      'CRASH-008': 'crash-reconciliation',
    },
    source: 'src/Wanxiangshu/OpenCode/Host/QuiescenceSurface.fs',
    representation: 'opaque-capability',
    kind: 'resource',
  },
  {
    module: 'OpenCode/Host/SessionRecoveryHostSurface.js',
    owner: 'crash-reconciliation',
    laws: ['CRASH-018', 'CHATEXEC-012', 'PROVIDER-ATTEMPT-RECOVERY-023'],
    lawOwners: { 'CHATEXEC-012': 'managed-chat-execution', 'PROVIDER-ATTEMPT-RECOVERY-023': 'provider-attempt-recovery' },
    source: 'src/Wanxiangshu/OpenCode/Host/SessionRecoveryHostSurface.fs',
    representation: 'opaque-capability',
    kind: 'resource',
  },
  {
    module: 'OpenCode/Tools/ExecutorToolSurface.js',
    owner: 'process-execution',
    laws: ['PROC-011'],
    source: 'src/Wanxiangshu/OpenCode/Tools/ExecutorToolSurface.fs',
    representation: 'json',
    kind: 'pure',
  },
  {
    module: 'Process/LargeGateSurface.js',
    owner: 'process-execution',
    laws: ['PROC-016'],
    source: 'src/Wanxiangshu/Process/LargeGateSurface.fs',
    representation: 'opaque-capability',
    kind: 'resource',
  },
  {
    module: 'Repository/Knowledge/Casebook/Surface.js',
    owner: 'knowledge-reuse',
    laws: ['DURABLE-EVENTS-019', 'DURABLE-CONVERGENCE-007', 'KNOWLEDGE-REUSE-002', 'KNOWLEDGE-REUSE-003', 'KNOWLEDGE-REUSE-004', 'KNOWLEDGE-REUSE-007', 'KNOWLEDGE-REUSE-008', 'KNOWLEDGE-REUSE-010', 'KNOWLEDGE-REUSE-014', 'KNOWLEDGE-REUSE-016'],
    lawOwners: {
      'DURABLE-EVENTS-019': 'durable-events',
      'DURABLE-CONVERGENCE-007': 'durable-convergence',
    },
    source: 'src/Wanxiangshu/Repository/Knowledge/Casebook/Surface.fs',
    representation: 'opaque-capability',
    kind: 'pure',
  },
  {
    module: 'Repository/Knowledge/Casebook/IndexSurface.js',
    owner: 'knowledge-reuse',
    laws: ['KNOWLEDGE-REUSE-012', 'MANAGED-SESSION-LIFECYCLE-022'],
    lawOwners: { 'MANAGED-SESSION-LIFECYCLE-022': 'managed-session-lifecycle' },
    source: 'src/Wanxiangshu/Repository/Knowledge/Casebook/IndexSurface.fs',
    representation: 'json',
    kind: 'pure',
  },
  {
    module: 'Repository/Knowledge/Casebook/BookkeeperRefreshSurface.js',
    owner: 'knowledge-reuse',
    laws: ['KNOWLEDGE-REUSE-006', 'KNOWLEDGE-REUSE-010'],
    source: 'src/Wanxiangshu/Repository/Knowledge/Casebook/BookkeeperRefreshSurface.fs',
    representation: 'opaque-capability',
    kind: 'resource',
  },
  {
    module: 'Repository/Knowledge/Casebook/BookkeeperSurface.js',
    owner: 'knowledge-reuse',
    laws: ['KNOWLEDGE-REUSE-006', 'KNOWLEDGE-REUSE-010'],
    source: 'src/Wanxiangshu/Repository/Knowledge/Casebook/BookkeeperSurface.fs',
    representation: 'json',
    kind: 'resource',
  },
  {
    module: 'Repository/Knowledge/Casebook/LifecycleSurface.js',
    owner: 'knowledge-reuse',
    laws: ['KNOWLEDGE-REUSE-006', 'KNOWLEDGE-REUSE-010', 'KNOWLEDGE-REUSE-013'],
    source: 'src/Wanxiangshu/Repository/Knowledge/Casebook/LifecycleSurface.fs',
    representation: 'opaque-capability',
    kind: 'resource',
  },
  {
    module: 'Repository/Knowledge/Casebook/SettlementSurface.js',
    owner: 'knowledge-reuse',
    laws: ['KNOWLEDGE-REUSE-001', 'KNOWLEDGE-REUSE-004', 'KNOWLEDGE-REUSE-005', 'KNOWLEDGE-REUSE-006', 'KNOWLEDGE-REUSE-009', 'KNOWLEDGE-REUSE-011', 'KNOWLEDGE-REUSE-013', 'KNOWLEDGE-REUSE-015', 'KNOWLEDGE-REUSE-016'],
    source: 'src/Wanxiangshu/Repository/Knowledge/Casebook/SettlementSurface.fs',
    representation: 'opaque-capability',
    kind: 'resource',
  },
  {
    module: 'Repository/Knowledge/Casebook/FetchSurface.js',
    owner: 'knowledge-reuse',
    laws: ['KNOWLEDGE-REUSE-001', 'KNOWLEDGE-REUSE-004', 'KNOWLEDGE-REUSE-005', 'KNOWLEDGE-REUSE-006', 'KNOWLEDGE-REUSE-009', 'KNOWLEDGE-REUSE-011', 'KNOWLEDGE-REUSE-013', 'KNOWLEDGE-REUSE-015', 'KNOWLEDGE-REUSE-016'],
    source: 'src/Wanxiangshu/Repository/Knowledge/Casebook/FetchSurface.fs',
    representation: 'opaque-capability',
    kind: 'resource',
  },
  {
    module: 'Participant/Provider/LanguageSurface.js',
    owner: 'provider-language',
    laws: [
      'PROVIDER-LANGUAGE-001',
      'PROVIDER-LANGUAGE-002',
      'PROVIDER-LANGUAGE-003',
      'PROVIDER-LANGUAGE-004',
      'PROVIDER-LANGUAGE-005',
      'PROVIDER-LANGUAGE-006',
      'PROVIDER-LANGUAGE-007',
      'PROVIDER-LANGUAGE-008',
      'PROVIDER-LANGUAGE-009',
      'PROVIDER-LANGUAGE-010',
      'PROVIDER-LANGUAGE-011',
    ],
    source: 'src/Wanxiangshu/Participant/Provider/LanguageSurface.fs',
    representation: 'json',
    kind: 'resource',
  },
  {
    module: 'Sphinx/V2/Runtime/Surface.js',
    owner: 'sphinx-v2',
    laws: [
      'SPHINX-V2-002',
      'SPHINX-V2-004',
      'SPHINX-V2-010',
      'SPHINX-V2-013',
      'SPHINX-V2-017',
      'SPHINX-V2-021',
      'SPHINX-V2-029',
      'SPHINX-V2-030',
      'SPHINX-V2-034',
    ],
    source: 'src/Wanxiangshu/Sphinx/V2/Runtime/Surface.fs',
    representation: 'json',
    kind: 'pure',
  },
  {
    module: 'Sphinx/V2/Persistence/Surface.js',
    owner: 'sphinx-v2',
    laws: ['SPHINX-V2-001', 'SPHINX-V2-005', 'SPHINX-V2-006', 'SPHINX-V2-007', 'SPHINX-V2-011', 'SPHINX-V2-019', 'SPHINX-V2-033'],
    source: 'src/Wanxiangshu/Sphinx/V2/Persistence/Surface.fs',
    representation: 'json',
    kind: 'resource',
  },
  {
    module: 'Sphinx/V2/Hosts/Mcp/Tool.js',
    owner: 'sphinx-v2',
    laws: ['SPHINX-V2-018', 'SPHINX-V2-036'],
    source: 'src/Wanxiangshu/Sphinx/V2/Hosts/Mcp/Tool.fs',
    representation: 'json',
    kind: 'pure',
  },
  {
    module: 'Sphinx/V2/Hosts/Mcp/Surface.js',
    owner: 'sphinx-v2',
    laws: ['SPHINX-V2-036', 'DURABLE-EVENTS-024'],
    lawOwners: { 'DURABLE-EVENTS-024': 'durable-events' },
    source: 'src/Wanxiangshu/Sphinx/V2/Hosts/Mcp/Surface.fs',
    representation: 'opaque-capability',
    kind: 'resource',
  },
  {
    module: 'Sphinx/V2/Hosts/OpenCode/Surface.js',
    owner: 'sphinx-v2',
    laws: ['SPHINX-V2-034'],
    source: 'src/Wanxiangshu/Sphinx/V2/Hosts/OpenCode/Surface.fs',
    representation: 'json',
    kind: 'pure',
  },
  {
    module: 'Sphinx/V2/Composition/SettlementSurface.js',
    owner: 'sphinx-v2',
    laws: ['SPHINX-V2-009', 'SPHINX-V2-019', 'SPHINX-V2-036', 'DURABLE-EVENTS-024'],
    lawOwners: { 'DURABLE-EVENTS-024': 'durable-events' },
    source: 'src/Wanxiangshu/Sphinx/V2/Composition/SettlementSurface.fs',
    representation: 'opaque-capability',
    kind: 'resource',
  },
  {
    module: 'Sphinx/V2/Wire/Surface.js',
    owner: 'sphinx-v2',
    laws: ['SPHINX-V2-009', 'SPHINX-V2-011', 'SPHINX-V2-020', 'SPHINX-V2-033', 'SPHINX-V2-036', 'DURABLE-EVENTS-024'],
    lawOwners: { 'DURABLE-EVENTS-024': 'durable-events' },
    source: 'src/Wanxiangshu/Sphinx/V2/Wire/Surface.fs',
    representation: 'opaque-capability',
    kind: 'resource',
  },
  {
    module: 'Sphinx/V2/Plugins/Bayes/Surface.js',
    owner: 'sphinx-v2',
    laws: ['SPHINX-V2-021', 'SPHINX-V2-025', 'SPHINX-V2-026'],
    source: 'src/Wanxiangshu/Sphinx/V2/Plugins/Bayes/Surface.fs',
    representation: 'json',
    kind: 'pure',
  },
  {
    module: 'Sphinx/V2/Plugins/AStar/Surface.js',
    owner: 'sphinx-v2',
    laws: ['SPHINX-V2-026'],
    source: 'src/Wanxiangshu/Sphinx/V2/Plugins/AStar/Surface.fs',
    representation: 'json',
    kind: 'pure',
  },
  {
    module: 'Sphinx/V2/Plugins/Mcts/Surface.js',
    owner: 'sphinx-v2',
    laws: ['SPHINX-V2-013', 'SPHINX-V2-026'],
    source: 'src/Wanxiangshu/Sphinx/V2/Plugins/Mcts/Surface.fs',
    representation: 'json',
    kind: 'pure',
  },
  {
    module: 'Sphinx/V2/Plugins/Ordinal/Surface.js',
    owner: 'sphinx-v2',
    laws: [
      'SPHINX-V2-014',
      'SPHINX-V2-023',
      'SPHINX-V2-024',
      'SPHINX-V2-025',
    ],
    source: 'src/Wanxiangshu/Sphinx/V2/Plugins/Ordinal/Surface.fs',
    representation: 'json',
    kind: 'pure',
  },
  {
    module: 'Sphinx/V2/Core/Surface.js',
    owner: 'sphinx-v2',
    laws: [
      'SPHINX-V2-001',
      'SPHINX-V2-003',
      'SPHINX-V2-004',
      'SPHINX-V2-005',
      'SPHINX-V2-006',
      'SPHINX-V2-007',
      'SPHINX-V2-010',
      'SPHINX-V2-015',
      'SPHINX-V2-016',
      'SPHINX-V2-019',
      'SPHINX-V2-020',
      'SPHINX-V2-025',
    ],
    source: 'src/Wanxiangshu/Sphinx/V2/Core/Surface.fs',
    representation: 'json',
    kind: 'pure',
  },
  {
    module: 'Context/Prefix/Surface.js',
    owner: 'prefix-stability',
    laws: [
      'PREFIX-STABILITY-001',
      'PREFIX-STABILITY-002',
      'PREFIX-STABILITY-003',
      'PREFIX-STABILITY-004',
      'PREFIX-STABILITY-006',
      'PREFIX-STABILITY-008',
      'PREFIX-STABILITY-011',
      'PREFIX-STABILITY-013',
      'PREFIX-STABILITY-015',
    ],
    source: 'src/Wanxiangshu/Context/Prefix/Surface.fs',
    representation: 'json',
    kind: 'pure',
  },
  {
    module: 'Context/Prefix/XWireSurface.js',
    owner: 'prefix-stability',
    laws: [
      'PREFIX-STABILITY-001',
      'PREFIX-STABILITY-003',
      'PREFIX-STABILITY-005',
      'PREFIX-STABILITY-009',
    ],
    source: 'src/Wanxiangshu/Context/Prefix/XWireSurface.fs',
    representation: 'json',
    kind: 'pure',
  },
  {
    module: 'OpenCode/Host/PluginRecoveryScopeSurface.js',
    owner: 'host-boundary',
    laws: ['PAR-011', 'PAR-020'],
    lawOwners: {
      'PAR-011': 'provider-attempt-recovery',
      'PAR-020': 'provider-attempt-recovery',
    },
    source: 'src/Wanxiangshu/OpenCode/Host/PluginRecoveryScopeSurface.fs',
    representation: 'opaque-capability',
    kind: 'resource',
  },
  {
    module: 'Repository/Investigation/WarmStartSurface.js',
    owner: 'repository-investigation',
    laws: [
      'REPOSITORY-INVESTIGATION-001',
      'REPOSITORY-INVESTIGATION-006',
      'REPOSITORY-INVESTIGATION-007',
      'REPOSITORY-INVESTIGATION-008',
      'REPOSITORY-INVESTIGATION-009',
    ],
    source: 'src/Wanxiangshu/Repository/Investigation/WarmStartSurface.fs',
    representation: 'opaque-capability',
    kind: 'resource',
  },
  {
    module: 'Repository/Investigation/SembleSurface.js',
    owner: 'repository-investigation',
    laws: ['REPOSITORY-INVESTIGATION-001', 'REPOSITORY-INVESTIGATION-002', 'REPOSITORY-INVESTIGATION-006'],
    source: 'src/Wanxiangshu/Repository/Investigation/SembleSurface.fs',
    representation: 'opaque-capability',
    kind: 'resource',
  },
  {
    module: 'OpenCode/Host/LoopSensorSurface.js',
    owner: 'degeneration-guard',
    laws: ['DG-001', 'DG-002', 'DG-006', 'DG-007', 'DG-008', 'DG-009', 'DG-010', 'DG-013'],
    source: 'src/Wanxiangshu/OpenCode/Host/LoopSensorSurface.fs',
    representation: 'opaque-capability',
    kind: 'resource',
  },
  {
    module: 'Composition/Turn/ReconcileSurface.js',
    owner: 'structured-workflow',
    laws: ['STRUCTURED-WORKFLOW-004'],
    source: 'src/Wanxiangshu/Composition/Turn/ReconcileSurface.fs',
    representation: 'opaque-capability',
    kind: 'resource',
  },
  {
    module: 'Repository/Programming/Js/GeneratorSurface.js',
    owner: 'repository-programming',
    laws: [
      'REPOSITORY-PROGRAMMING-001',
      'REPOSITORY-PROGRAMMING-002',
      'REPOSITORY-PROGRAMMING-003',
      'REPOSITORY-PROGRAMMING-004',
      'REPOSITORY-PROGRAMMING-005',
    ],
    source: 'src/Wanxiangshu/Repository/Programming/Js/GeneratorSurface.fs',
    representation: 'json',
    kind: 'pure',
  },
  {
    module: 'Repository/Programming/Js/RuntimeSurface.js',
    owner: 'repository-programming',
    laws: ['REPOSITORY-PROGRAMMING-006', 'REPOSITORY-PROGRAMMING-012'],
    source: 'src/Wanxiangshu/Repository/Programming/Js/RuntimeSurface.fs',
    representation: 'opaque-capability',
    kind: 'resource',
  },
  {
    module: 'Repository/Programming/Js/FilesystemSurface.js',
    owner: 'repository-programming',
    laws: [
      'REPOSITORY-PROGRAMMING-007',
      'REPOSITORY-PROGRAMMING-008',
      'REPOSITORY-PROGRAMMING-009',
      'REPOSITORY-PROGRAMMING-013',
      'REPOSITORY-PROGRAMMING-015',
    ],
    source: 'src/Wanxiangshu/Repository/Programming/Js/FilesystemSurface.fs',
    representation: 'opaque-capability',
    kind: 'resource',
  },
  {
    module: 'Repository/Programming/Js/TransactionSurface.js',
    owner: 'repository-programming',
    laws: [
      'DURABLE-EVENTS-019',
      'DURABLE-CONVERGENCE-007',
      'REPOSITORY-PROGRAMMING-007',
      'REPOSITORY-PROGRAMMING-010',
      'REPOSITORY-PROGRAMMING-013',
      'REPOSITORY-PROGRAMMING-014',
      'REPOSITORY-PROGRAMMING-015',
      'REPOSITORY-PROGRAMMING-018',
    ],
    lawOwners: {
      'DURABLE-EVENTS-019': 'durable-events',
      'DURABLE-CONVERGENCE-007': 'durable-convergence',
    },
    source: 'src/Wanxiangshu/Repository/Programming/Js/TransactionSurface.fs',
    representation: 'opaque-capability',
    kind: 'resource',
  },
  {
    module: 'Repository/Programming/Js/WorkflowSurface.js',
    owner: 'repository-programming',
    laws: [
      'REPOSITORY-PROGRAMMING-011',
      'REPOSITORY-PROGRAMMING-012',
      'REPOSITORY-PROGRAMMING-013',
      'REPOSITORY-PROGRAMMING-014',
      'REPOSITORY-PROGRAMMING-015',
      'REPOSITORY-PROGRAMMING-016',
      'REPOSITORY-PROGRAMMING-018',
      'REPOSITORY-PROGRAMMING-019',
    ],
    source: 'src/Wanxiangshu/Repository/Programming/Js/WorkflowSurface.fs',
    representation: 'opaque-capability',
    kind: 'resource',
  },
  {
    module: 'Repository/Programming/Js/OpenCode/ToolHostSurface.js',
    owner: 'repository-programming',
    laws: ['REPOSITORY-PROGRAMMING-005', 'REPOSITORY-PROGRAMMING-016'],
    source: 'src/Wanxiangshu/Repository/Programming/Js/OpenCode/ToolHostSurface.fs',
    representation: 'opaque-capability',
    kind: 'resource',
  },
  {
    module: 'OpenCode/Tools/FileToolsSurface.js',
    owner: 'repository-programming',
    laws: ['REPOSITORY-PROGRAMMING-007', 'REPOSITORY-PROGRAMMING-010'],
    source: 'src/Wanxiangshu/OpenCode/Tools/FileToolsSurface.fs',
    representation: 'opaque-capability',
    kind: 'resource',
  },
  {
    module: 'OpenCode/Tools/FileMutationSurface.js',
    owner: 'repository-programming',
    laws: ['REPOSITORY-PROGRAMMING-020'],
    source: 'src/Wanxiangshu/OpenCode/Tools/FileMutationSurface.fs',
    representation: 'opaque-capability',
    kind: 'resource',
  },
  {
    module: 'Context/Companion/Blogger/FrameSurface.js',
    owner: 'context-compression',
    laws: ['CONTEXT-COMPRESSION-011', 'CONTEXT-COMPRESSION-012', 'CONTEXT-COMPRESSION-015', 'CONTEXT-COMPRESSION-016'],
    source: 'src/Wanxiangshu/Context/Companion/Blogger/FrameSurface.fs',
    representation: 'json',
    kind: 'pure',
  },
  {
    module: 'Context/Companion/Blogger/DeltaSurface.js',
    owner: 'context-compression',
    laws: ['CONTEXT-COMPRESSION-003', 'CONTEXT-COMPRESSION-012', 'CONTEXT-COMPRESSION-016'],
    source: 'src/Wanxiangshu/Context/Companion/Blogger/DeltaSurface.fs',
    representation: 'json',
    kind: 'pure',
  },
  {
    module: 'Context/Companion/ProjectionSurface.js',
    owner: 'context-compression',
    laws: ['CONTEXT-COMPRESSION-011', 'CONTEXT-COMPRESSION-012'],
    source: 'src/Wanxiangshu/Context/Companion/ProjectionSurface.fs',
    representation: 'json',
    kind: 'pure',
  },
  {
    module: 'Context/Companion/CompressionSurface.js',
    owner: 'context-compression',
    laws: [
      'CONTEXT-COMPRESSION-002',
      'CONTEXT-COMPRESSION-005',
      'CONTEXT-COMPRESSION-006',
      'CONTEXT-COMPRESSION-007',
      'CONTEXT-COMPRESSION-008',
      'CONTEXT-COMPRESSION-009',
      'CONTEXT-COMPRESSION-010',
      'CONTEXT-COMPRESSION-013',
    ],
    source: 'src/Wanxiangshu/Context/Companion/CompressionSurface.fs',
    representation: 'json',
    kind: 'pure',
  },
  {
    module: 'Context/Companion/RuntimeSurface.js',
    owner: 'context-compression',
    laws: ['CONTEXT-COMPRESSION-006', 'CONTEXT-COMPRESSION-018'],
    source: 'src/Wanxiangshu/Context/Companion/RuntimeSurface.fs',
    representation: 'opaque-capability',
    kind: 'resource',
  },
  {
    module: 'Persistence/Journal/ObligationJournalSurface.js',
    owner: 'obligation-ledger',
    laws: ['OBLIGATION-LEDGER-007'],
    source: 'src/Wanxiangshu/Persistence/Journal/ObligationJournalSurface.fs',
    representation: 'opaque-capability',
    kind: 'resource',
  },
  {
    module: 'Persistence/Journal/ObligationEnvelopeSurface.js',
    owner: 'obligation-ledger',
    laws: ['OBLIGATION-LEDGER-007'],
    source: 'src/Wanxiangshu/Persistence/Journal/ObligationEnvelopeSurface.fs',
    representation: 'json',
    kind: 'pure',
  },
  {
    module: 'OpenCode/Host/OpenCodeContract.js',
    owner: 'dispatch-protocol',
    laws: ['DISPATCH-PROTOCOL-009'],
    source: 'src/Wanxiangshu/OpenCode/Host/OpenCodeContract.fs',
    representation: 'json',
    kind: 'pure',
  },
  {
    module: 'Mission/WorkRecord/OpeningSemanticSurface.js',
    owner: 'work-record',
    laws: ["WORK-RECORD-009"],
    source: 'src/Wanxiangshu/Mission/WorkRecord/OpeningSemanticSurface.fs',
    representation: 'json',
    kind: 'pure',
  },
  {
    module: 'Persistence/EventStore/CodecSurface.js',
    owner: 'durable-events',
    laws: ['DURABLE-EVENTS-003', 'DURABLE-EVENTS-005', 'DURABLE-EVENTS-023'],
    source: 'src/Wanxiangshu/Persistence/EventStore/CodecSurface.fs',
    representation: 'json',
    kind: 'pure',
  },
  {
    module: 'Persistence/Journal/CodecSurface.js',
    owner: 'durable-events',
    laws: ['DURABLE-EVENTS-001', 'DURABLE-EVENTS-002', 'DURABLE-EVENTS-003', 'DURABLE-EVENTS-014'],
    source: 'src/Wanxiangshu/Persistence/Journal/CodecSurface.fs',
    representation: 'json',
    kind: 'pure',
  },
  {
    module: 'Persistence/Journal/FactCodecSurface.js',
    owner: 'durable-events',
    laws: ['DURABLE-EVENTS-002', 'DURABLE-EVENTS-003', 'DURABLE-EVENTS-005', 'MANAGED-SESSION-009'],
    lawOwners: { 'MANAGED-SESSION-009': 'managed-session-lifecycle' },
    source: 'src/Wanxiangshu/Persistence/Journal/FactCodecSurface.fs',
    representation: 'json',
    kind: 'pure',
  },
  {
    module: 'Persistence/Journal/Surface.js',
    owner: 'durable-events',
    laws: ['DURABLE-EVENTS-009', 'DURABLE-EVENTS-010', 'DURABLE-EVENTS-012', 'DURABLE-EVENTS-013', 'DURABLE-EVENTS-019', 'EFFECT-ACCOUNTING-008'],
    lawOwners: {
      'EFFECT-ACCOUNTING-008': 'effect-accounting',
    },
    source: 'src/Wanxiangshu/Persistence/Journal/Surface.fs',
    representation: 'opaque-capability',
    kind: 'resource',
  },
  {
    module: 'Persistence/EventStore/Surface.js',
    owner: 'durable-events',
    laws: ['DURABLE-EVENTS-001', 'DURABLE-EVENTS-004', 'DURABLE-EVENTS-005', 'DURABLE-EVENTS-006', 'DURABLE-EVENTS-013', 'DURABLE-EVENTS-019', 'DURABLE-EVENTS-024', 'DURABLE-CONVERGENCE-007'],
    lawOwners: { 'DURABLE-CONVERGENCE-007': 'durable-convergence' },
    source: 'src/Wanxiangshu/Persistence/EventStore/Surface.fs',
    representation: 'opaque-capability',
    kind: 'resource',
  },
  {
    module: 'Persistence/EventStore/MergeSurface.js',
    owner: 'durable-convergence',
    laws: ['DURABLE-CONVERGENCE-001', 'DURABLE-CONVERGENCE-002', 'DURABLE-CONVERGENCE-003', 'DURABLE-CONVERGENCE-006'],
    source: 'src/Wanxiangshu/Persistence/EventStore/MergeSurface.fs',
    representation: 'json',
    kind: 'pure',
  },
  {
    module: 'Persistence/EventStore/RetentionSurface.js',
    owner: 'durable-convergence',
    laws: ['DURABLE-CONVERGENCE-009', 'DURABLE-CONVERGENCE-010', 'DURABLE-CONVERGENCE-011'],
    source: 'src/Wanxiangshu/Persistence/EventStore/RetentionSurface.fs',
    representation: 'json',
    kind: 'resource',
  },
  {
    module: 'Process/DeadlineSurface.js',
    owner: 'time-capability',
    laws: ['TIME-002', 'TIME-005'],
    source: 'src/Wanxiangshu/Process/DeadlineSurface.fs',
    representation: 'opaque-capability',
    kind: 'pure',
  },
  {
    module: 'Interaction/Authority/RuntimeSurface.js',
    owner: 'interaction-authority',
    laws: [
      'INTERACTION-AUTHORITY-001',
      'INTERACTION-AUTHORITY-002',
      'INTERACTION-AUTHORITY-003',
      'INTERACTION-AUTHORITY-004',
      'INTERACTION-AUTHORITY-005',
      'INTERACTION-AUTHORITY-006',
      'INTERACTION-AUTHORITY-007',
      'INTERACTION-AUTHORITY-008',
      'INTERACTION-AUTHORITY-009',
      'INTERACTION-AUTHORITY-010',
      'INTERACTION-AUTHORITY-011',
      'INTERACTION-AUTHORITY-012',
      'INTERACTION-AUTHORITY-013',
      'INTERACTION-AUTHORITY-014',
      'INTERACTION-AUTHORITY-015',
      'INTERACTION-AUTHORITY-016',
      'INTERACTION-AUTHORITY-017',
      'INTERACTION-AUTHORITY-018',
      'MANAGED-SESSION-020',
      'PID-004',
      'PID-008',
      'PID-009',
    ],
    lawOwners: {
      'MANAGED-SESSION-020': 'managed-session-lifecycle',
      'PID-004': 'participant-identity',
      'PID-008': 'participant-identity',
      'PID-009': 'participant-identity',
    },
    source: 'src/Wanxiangshu/Interaction/Authority/RuntimeSurface.fs',
    representation: 'json',
    kind: 'pure',
  },
  {
    module: 'Interaction/Attention/Surface.js',
    owner: 'attention-regulation',
    laws: [
      'ATTENTION-REGULATION-003',
      'ATTENTION-REGULATION-004',
      'ATTENTION-REGULATION-005',
      'ATTENTION-REGULATION-006',
    ],
    source: 'src/Wanxiangshu/Interaction/Attention/Surface.fs',
    representation: 'opaque-capability',
    kind: 'resource',
  },
  {
    module: 'OpenCode/Tools/AttentionToolSurface.js',
    owner: 'attention-regulation',
    laws: [
      'ATTENTION-REGULATION-001',
      'ATTENTION-REGULATION-002',
      'ATTENTION-REGULATION-003',
      'ATTENTION-REGULATION-004',
    ],
    source: 'src/Wanxiangshu/OpenCode/Tools/AttentionToolSurface.fs',
    representation: 'opaque-capability',
    kind: 'resource',
  },
  {
    module: 'Interaction/Concern/Surface.js',
    owner: 'concern-routing',
    laws: [
      'CONCERN-ROUTING-001',
      'CONCERN-ROUTING-002',
      'CONCERN-ROUTING-003',
      'CONCERN-ROUTING-004',
      'CONCERN-ROUTING-005',
      'CONCERN-ROUTING-006',
      'CONCERN-ROUTING-007',
    ],
    source: 'src/Wanxiangshu/Interaction/Concern/Surface.fs',
    representation: 'opaque-capability',
    kind: 'resource',
  },
  {
    module: 'Interaction/Repair/CompletedTurnSurface.js',
    owner: 'interaction-authority',
    laws: ['INTERACTION-AUTHORITY-004', 'INTERACTION-AUTHORITY-019'],
    source: 'src/Wanxiangshu/Interaction/Repair/CompletedTurnSurface.fs',
    representation: 'json',
    kind: 'pure',
  },
  {
    module: 'OpenCode/Host/ChatParamsSurface.js',
    owner: 'interaction-authority',
    laws: ['INTERACTION-AUTHORITY-011'],
    source: 'src/Wanxiangshu/OpenCode/Host/ChatParamsSurface.fs',
    representation: 'json',
    kind: 'pure',
  },
  {
    module: 'OpenCode/Host/SessionsSurface.js',
    owner: 'session-ontology',
    laws: ['SESSION-ONTOLOGY-006', 'MANAGED-SESSION-003', 'MANAGED-SESSION-016', 'MANAGED-SESSION-017'],
    lawOwners: {
      'MANAGED-SESSION-003': 'managed-session-lifecycle',
      'MANAGED-SESSION-016': 'managed-session-lifecycle',
      'MANAGED-SESSION-017': 'managed-session-lifecycle',
    },
    source: 'src/Wanxiangshu/OpenCode/Host/SessionsSurface.fs',
    representation: 'json',
    kind: 'resource',
  },
  {
    module: 'OpenCode/Host/TerminalPolicySurface.js',
    owner: 'managed-session-lifecycle',
    laws: ['MANAGED-SESSION-006'],
    source: 'src/Wanxiangshu/OpenCode/Host/TerminalPolicySurface.fs',
    representation: 'json',
    kind: 'pure',
  },
  {
    module: 'Participant/Provider/Projection/Surface.js',
    owner: 'provider-projection',
    laws: [
      'PROVIDER-PROJECTION-001',
      'PROVIDER-PROJECTION-002',
      'PROVIDER-PROJECTION-003',
      'PROVIDER-PROJECTION-004',
      'PROVIDER-PROJECTION-005',
      'PROVIDER-PROJECTION-006',
      'PROVIDER-PROJECTION-007',
      'PROVIDER-PROJECTION-011',
      'PROVIDER-PROJECTION-012',
    ],
    source: 'src/Wanxiangshu/Participant/Provider/Projection/Surface.fs',
    representation: 'json',
    kind: 'pure',
  },
  {
    module: 'Persistence/Journal/RevisionSurface.js',
    owner: 'durable-events',
    laws: ['DURABLE-EVENTS-013'],
    source: 'src/Wanxiangshu/Persistence/Journal/RevisionSurface.fs',
    representation: 'opaque-capability',
    kind: 'resource',
  },
  {
    module: 'Execution/Delegation/HostTurnObservedSurface.js',
    owner: 'durable-events',
    laws: ['DURABLE-EVENTS-002'],
    source: 'src/Wanxiangshu/Execution/Delegation/HostTurnObservedSurface.fs',
    representation: 'json',
    kind: 'pure',
  },
  {
    module: 'Context/Companion/FoldSurface.js',
    owner: 'context-compression',
    laws: ['DURABLE-EVENTS-015'],
    lawOwners: { 'DURABLE-EVENTS-015': 'durable-events' },
    source: 'src/Wanxiangshu/Context/Companion/FoldSurface.fs',
    representation: 'json',
    kind: 'pure',
  },
  {
    module: 'Execution/Session/AssociationSurface.js',
    owner: 'durable-events',
    laws: ['DURABLE-EVENTS-013'],
    source: 'src/Wanxiangshu/Execution/Session/AssociationSurface.fs',
    representation: 'json',
    kind: 'pure',
  },
  {
    module: 'Git/Hook/Surface.js',
    owner: 'durable-events',
    laws: ['DURABLE-EVENTS-018'],
    source: 'src/Wanxiangshu/Git/Hook/Surface.fs',
    representation: 'json',
    kind: 'resource',
  },
  {
    module: 'OpenCode/Host/WorkspaceSharedJournal.js',
    owner: 'durable-events',
    laws: ['DURABLE-EVENTS-009', 'DURABLE-EVENTS-010'],
    source: 'src/Wanxiangshu/OpenCode/Host/WorkspaceSharedJournal.fs',
    representation: 'opaque-capability',
    kind: 'resource',
  },
  {
    module: 'OpenCode/Codec/CanonicalJsonSurface.js',
    owner: 'durable-events',
    laws: ['DURABLE-EVENTS-003'],
    source: 'src/Wanxiangshu/OpenCode/Codec/CanonicalJsonSurface.fs',
    representation: 'json',
    kind: 'pure',
  },
  {
    module: 'OpenCode/Codec/ProviderProjectionSurface.js',
    owner: 'provider-projection',
    laws: ['PROVIDER-PROJECTION-003', 'HOST-BOUNDARY-020'],
    lawOwners: { 'HOST-BOUNDARY-020': 'host-boundary' },
    source: 'src/Wanxiangshu/OpenCode/Codec/ProviderProjectionSurface.fs',
    representation: 'json',
    kind: 'resource',
  },
  {
    module: 'OpenCode/Codec/ToolHostSurface.js',
    owner: 'provider-projection',
    laws: ['PROVIDER-PROJECTION-003', 'PROVIDER-PROJECTION-005', 'PROVIDER-PROJECTION-008', 'PROVIDER-PROJECTION-009', 'HOST-BOUNDARY-009', 'HOST-BOUNDARY-030'],
    lawOwners: { 'HOST-BOUNDARY-009': 'host-boundary', 'HOST-BOUNDARY-030': 'host-boundary' },
    source: 'src/Wanxiangshu/OpenCode/Codec/ToolHostSurface.fs',
    representation: 'opaque-capability',
    kind: 'resource',
  },
  {
    module: 'OpenCode/Host/PairProgrammingThoughtSurface.js',
    owner: 'provider-projection',
    laws: ['PROVIDER-PROJECTION-010'],
    source: 'src/Wanxiangshu/OpenCode/Host/PairProgrammingThoughtSurface.fs',
    representation: 'opaque-capability',
    kind: 'pure',
  },
  {
    module: 'Requirement/Grounding/Surface.js',
    owner: 'requirement-grounding',
    laws: [
      'requirement-grounding-001',
      'requirement-grounding-002',
      'requirement-grounding-003',
      'requirement-grounding-004',
      'requirement-grounding-005',
      'requirement-grounding-006',
    ],
    source: 'src/Wanxiangshu/Requirement/Grounding/Surface.fs',
    representation: 'json',
    kind: 'pure',
  },
  {
    module: 'OpenCode/Host/RequirementGroundingSurface.js',
    owner: 'requirement-grounding',
    laws: [
      'requirement-grounding-006',
      'requirement-grounding-007',
      'requirement-grounding-008',
      'requirement-grounding-011',
      'requirement-grounding-012',
    ],
    source: 'src/Wanxiangshu/OpenCode/Host/RequirementGroundingSurface.fs',
    representation: 'opaque-capability',
    kind: 'resource',
  },
  {
    module: 'OpenCode/Host/RequirementGroundingRepositorySurface.js',
    owner: 'requirement-grounding',
    laws: ['requirement-grounding-009', 'requirement-grounding-010'],
    source: 'src/Wanxiangshu/OpenCode/Host/RequirementGroundingRepositorySurface.fs',
    representation: 'opaque-capability',
    kind: 'resource',
  },
  {
    module: 'Enforcer/Surface.js',
    owner: 'behavior-diagnosis',
    laws: [
      'BD-001',
      'BD-002',
      'BD-003',
      'BD-004',
      'BD-005',
      'BD-006',
      'BD-007',
      'BD-008',
      'BD-009',
      'BD-010',
      'BD-011',
    ],
    source: 'src/Wanxiangshu/Enforcer/Surface.fs',
    representation: 'json',
    kind: 'resource',
  },
  {
    module: 'Enforcer/ObservationSurface.js',
    owner: 'behavior-diagnosis',
    laws: ['BD-012', 'BD-014', 'BD-015', 'BD-016'],
    source: 'src/Wanxiangshu/Enforcer/ObservationSurface.fs',
    representation: 'json',
    kind: 'pure',
  },
  {
    module: 'Enforcer/BlogSurface.js',
    owner: 'behavior-diagnosis',
    laws: ['BD-006', 'BD-008', 'BD-009', 'BD-010', 'BD-011', 'BD-013', 'BD-017'],
    source: 'src/Wanxiangshu/Enforcer/BlogSurface.fs',
    representation: 'json',
    kind: 'pure',
  },
  {
    module: 'Participant/Provider/Attempt/FailureSurface.js',
    owner: 'execution-failure-policy',
    laws: ['EXECFAIL-001', 'EXECFAIL-002', 'EXECFAIL-008'],
    source: 'src/Wanxiangshu/Participant/Provider/Attempt/FailureSurface.fs',
    representation: 'json',
    kind: 'pure',
  },
  {
    module: 'Participant/Provider/Attempt/PlannerSurface.js',
    owner: 'capability-enforcement',
    laws: ['ENF-001', 'ENF-003', 'ENF-004'],
    source: 'src/Wanxiangshu/Participant/Provider/Attempt/PlannerSurface.fs',
    representation: 'json',
    kind: 'pure',
  },
  {
    module: 'OpenCode/Host/ManagedAgentConfigSurface.js',
    owner: 'capability-enforcement',
    laws: ['ENF-010', 'ENF-011'],
    source: 'src/Wanxiangshu/OpenCode/Host/ManagedAgentConfigSurface.fs',
    representation: 'json',
    kind: 'resource',
  },
  {
    module: 'OpenCode/Tools/ToolRegistrySurface.js',
    owner: 'capability-enforcement',
    laws: ['ENF-001', 'ENF-006', 'ENF-010'],
    source: 'src/Wanxiangshu/OpenCode/Tools/ToolRegistrySurface.fs',
    representation: 'json',
    kind: 'pure',
  },
  {
    module: 'OpenCode/Tools/ToolSurface.js',
    owner: 'capability-enforcement',
    laws: ['ENF-006', 'ENF-010'],
    source: 'src/Wanxiangshu/OpenCode/Tools/ToolSurface.fs',
    representation: 'json',
    kind: 'pure',
  },
  {
    module: 'Ablation/Surface.js',
    owner: 'feature-ablation',
    laws: ['ABL-001', 'ABL-002', 'ABL-003', 'ABL-004'],
    source: 'src/Wanxiangshu/Ablation/Surface.fs',
    representation: 'json',
    kind: 'pure',
  },
  {
    module: 'Strength/Surface.js',
    owner: 'speculative-investigation',
    laws: [
      'DURABLE-EVENTS-019',
      'DURABLE-CONVERGENCE-007',
      'SPEC-INV-001',
      'SPEC-INV-002',
      'SPEC-INV-003',
      'SPEC-INV-004',
      'SPEC-INV-005',
      'SPEC-INV-006',
      'SPEC-INV-007',
      'SPEC-INV-008',
      'SPEC-INV-009',
      'SPEC-INV-010',
      'SPEC-INV-011',
      'SPEC-INV-012',
      'SPEC-INV-013',
      'SPEC-INV-014',
      'SPEC-INV-015',
      'SPEC-INV-016',
      'SPEC-INV-020',
    ],
    lawOwners: {
      'DURABLE-EVENTS-019': 'durable-events',
      'DURABLE-CONVERGENCE-007': 'durable-convergence',
    },
    source: 'src/Wanxiangshu/Strength/Surface.fs',
    representation: 'opaque-capability',
    kind: 'resource',
  },
  {
    module: 'Execution/Session/Wait/Surface.js',
    owner: 'causal-wait',
    laws: ['CAUSAL-001', 'CAUSAL-002', 'CAUSAL-003', 'CAUSAL-004', 'CAUSAL-005', 'CAUSAL-006', 'CAUSAL-007', 'CAUSAL-008', 'CAUSAL-009', 'PROC-008'],
    lawOwners: { 'PROC-008': 'process-execution' },
    source: 'src/Wanxiangshu/Execution/Session/Wait/Surface.fs',
    representation: 'opaque-capability',
    kind: 'resource',
  },
  {
    module: 'Change/Surface.js',
    owner: 'change-integration',
    laws: ['CHGINT-001', 'CHGINT-002', 'CHGINT-003', 'CHGINT-004', 'CHGINT-005', 'CHGINT-006', 'CHGINT-007', 'CHGINT-008', 'CHGINT-009', 'CHGINT-011', 'CHGINT-013', 'CHGINT-014', 'CHGINT-015', 'CRASH-019', 'DELEG-014', 'DELEG-015'],
    lawOwners: { 'CRASH-019': 'crash-reconciliation', 'DELEG-014': 'delegation', 'DELEG-015': 'delegation' },
    source: 'src/Wanxiangshu/Change/Surface.fs',
    representation: 'opaque-capability',
    kind: 'resource',
  },
  {
    module: 'Process/Surface.js',
    owner: 'time-capability',
    laws: ['TIME-001', 'TIME-002', 'TIME-003', 'TIME-005', 'TIME-006', 'TIME-007', 'TIME-008'],
    source: 'src/Wanxiangshu/Process/Surface.fs',
    representation: 'opaque-capability',
    kind: 'resource',
  },
  {
    module: 'OpenCode/Host/EventsSurface.js',
    owner: 'host-boundary',
    laws: ['HOST-BOUNDARY-016'],
    source: 'src/Wanxiangshu/OpenCode/Host/EventsSurface.fs',
    representation: 'opaque-capability',
    kind: 'resource',
  },
  {
    module: 'OpenCode/Host/WorkspaceEventStoreSurface.js',
    owner: 'durable-events',
    laws: ['DURABLE-EVENTS-020'],
    source: 'src/Wanxiangshu/OpenCode/Host/WorkspaceEventStoreSurface.fs',
    representation: 'opaque-capability',
    kind: 'resource',
  },
  {
    module: 'OpenCode/Host/HostMessageProjection.js',
    owner: 'host-boundary',
    laws: ['HOST-BOUNDARY-011'],
    source: 'src/Wanxiangshu/OpenCode/Host/HostMessageProjection.fs',
    representation: 'json',
    kind: 'resource',
  },
  {
    module: 'OpenCode/Host/HostSessionContextSurface.js',
    owner: 'host-boundary',
    laws: ['HOST-BOUNDARY-017'],
    source: 'src/Wanxiangshu/OpenCode/Host/HostSessionContextSurface.fs',
    representation: 'json',
    kind: 'pure',
  },
  {
    module: 'OpenCode/Host/SharedStateSurface.js',
    owner: 'host-boundary',
    laws: ['HOST-BOUNDARY-010', 'HOST-BOUNDARY-031'],
    source: 'src/Wanxiangshu/OpenCode/Host/SharedStateSurface.fs',
    representation: 'opaque-capability',
    kind: 'resource',
  },
  {
    module: 'OpenCode/Host/ProviderRunBindingSurface.js',
    owner: 'host-boundary',
    laws: ['HOST-BOUNDARY-008'],
    source: 'src/Wanxiangshu/OpenCode/Host/ProviderRunBindingSurface.fs',
    representation: 'opaque-capability',
    kind: 'resource',
  },
  {
    module: 'OpenCode/Host/MessageVisibilitySurface.js',
    owner: 'host-boundary',
    laws: ['HOST-BOUNDARY-008'],
    source: 'src/Wanxiangshu/OpenCode/Host/MessageVisibilitySurface.fs',
    representation: 'opaque-capability',
    kind: 'resource',
  },
  {
    module: 'OpenCode/Plugin/PluginLifecycleSurface.js',
    owner: 'host-boundary',
    laws: ['MANAGED-SESSION-LIFECYCLE-022'],
    lawOwners: { 'MANAGED-SESSION-LIFECYCLE-022': 'managed-session-lifecycle' },
    source: 'src/Wanxiangshu/OpenCode/Plugin/PluginLifecycleSurface.fs',
    representation: 'opaque-capability',
    kind: 'resource',
  },
  {
    module: 'OpenCode/Plugin/PluginHostWiringSurface.js',
    owner: 'host-boundary',
    laws: ['KNOWLEDGE-REUSE-013'],
    lawOwners: { 'KNOWLEDGE-REUSE-013': 'knowledge-reuse' },
    source: 'src/Wanxiangshu/OpenCode/Plugin/PluginHostWiringSurface.fs',
    representation: 'opaque-capability',
    kind: 'resource',
  },
  {
    module: 'OpenCode/Host/PluginHooksSurface.js',
    owner: 'host-boundary',
    laws: ['HOST-BOUNDARY-014', 'EFFECT-ACCOUNTING-008'],
    lawOwners: { 'EFFECT-ACCOUNTING-008': 'effect-accounting' },
    source: 'src/Wanxiangshu/OpenCode/Host/PluginHooksSurface.fs',
    representation: 'opaque-capability',
    kind: 'resource',
  },
  {
    module: 'OpenCode/Host/SessionSnapshotSurface.js',
    owner: 'host-boundary',
    laws: ['HOST-BOUNDARY-006', 'HOST-BOUNDARY-009', 'HOST-BOUNDARY-012', 'HOST-BOUNDARY-019', 'HOST-BOUNDARY-020'],
    source: 'src/Wanxiangshu/OpenCode/Host/SessionSnapshotSurface.fs',
    representation: 'opaque-capability',
    kind: 'pure',
  },
  {
    module: 'OpenCode/Host/HostSignalSurface.js',
    owner: 'host-boundary',
    laws: ['HOST-BOUNDARY-001', 'HOST-BOUNDARY-002', 'HOST-BOUNDARY-003', 'HOST-BOUNDARY-027', 'HOST-BOUNDARY-030'],
    source: 'src/Wanxiangshu/OpenCode/Host/HostSignalSurface.fs',
    representation: 'json',
    kind: 'pure',
  },
  {
    module: 'OpenCode/Host/HostSignalSubscribeSurface.js',
    owner: 'host-boundary',
    laws: ['HOST-BOUNDARY-003', 'HOST-BOUNDARY-028'],
    source: 'src/Wanxiangshu/OpenCode/Host/HostSignalSubscribeSurface.fs',
    representation: 'opaque-capability',
    kind: 'resource',
  },
  {
    module: 'Execution/Failure/Surface.js',
    owner: 'execution-failure-policy',
    laws: ['EXECFAIL-001', 'EXECFAIL-002', 'EXECFAIL-003', 'EXECFAIL-004', 'EXECFAIL-005', 'EXECFAIL-006', 'EXECFAIL-007', 'EXECFAIL-008'],
    source: 'src/Wanxiangshu/Execution/Failure/Surface.fs',
    representation: 'json',
    kind: 'pure',
  },
  {
    module: 'Execution/Session/ChatExecution/Surface.js',
    owner: 'managed-chat-execution',
    laws: ['CHATEXEC-001', 'CHATEXEC-002', 'CHATEXEC-004', 'CHATEXEC-005', 'CHATEXEC-006', 'CHATEXEC-007', 'CHATEXEC-011', 'CHATEXEC-012'],
    source: 'src/Wanxiangshu/Execution/Session/ChatExecution/Surface.fs',
    representation: 'json',
    kind: 'pure',
  },
  {
    module: 'Execution/Session/ChatExecution/RecoverySurface.js',
    owner: 'managed-chat-execution',
    laws: ['CHATEXEC-012'],
    source: 'src/Wanxiangshu/Execution/Session/ChatExecution/RecoverySurface.fs',
    representation: 'json',
    kind: 'pure',
  },
  {
    module: 'Execution/Session/ChatExecution/RecoveryRuntimeSurface.js',
    owner: 'managed-chat-execution',
    laws: ['CHATEXEC-012'],
    source: 'src/Wanxiangshu/Execution/Session/ChatExecution/RecoveryRuntimeSurface.fs',
    representation: 'json',
    kind: 'resource',
  },
  {
    module: 'Execution/Session/ChatExecution/StatusSurface.js',
    owner: 'managed-chat-execution',
    laws: ['CHATEXEC-007', 'CHATEXEC-008'],
    source: 'src/Wanxiangshu/Execution/Session/ChatExecution/StatusSurface.fs',
    representation: 'json',
    kind: 'resource',
  },
  {
    module: 'Execution/Delegation/Handle/Surface.js',
    owner: 'managed-session-lifecycle',
    laws: ['MANAGED-SESSION-006', 'MANAGED-SESSION-007', 'MANAGED-SESSION-008', 'MANAGED-SESSION-009', 'MANAGED-SESSION-010', 'MANAGED-SESSION-013', 'MANAGED-SESSION-015'],
    source: 'src/Wanxiangshu/Execution/Delegation/Handle/Surface.fs',
    representation: 'opaque-capability',
    kind: 'pure',
  },
  {
    module: 'Execution/Delegation/Handle/FoldSurface.js',
    owner: 'managed-session-lifecycle',
    laws: ['MANAGED-SESSION-006', 'MANAGED-SESSION-008', 'MANAGED-SESSION-009', 'MANAGED-SESSION-015'],
    source: 'src/Wanxiangshu/Execution/Delegation/Handle/FoldSurface.fs',
    representation: 'opaque-capability',
    kind: 'pure',
  },
  {
    module: 'Execution/Delegation/Handle/JournalSurface.js',
    owner: 'managed-session-lifecycle',
    laws: ['MANAGED-SESSION-009'],
    source: 'src/Wanxiangshu/Execution/Delegation/Handle/JournalSurface.fs',
    representation: 'opaque-capability',
    kind: 'resource',
  },
  {
    module: 'Execution/Session/Attachment/AttachmentSurface.js',
    owner: 'managed-session-lifecycle',
    laws: ['MANAGED-SESSION-001', 'MANAGED-SESSION-005'],
    source: 'src/Wanxiangshu/Execution/Session/Attachment/AttachmentSurface.fs',
    representation: 'json',
    kind: 'pure',
  },
  {
    module: 'OpenCode/Host/SatelliteSurface.js',
    owner: 'managed-session-lifecycle',
    laws: ['MANAGED-SESSION-002', 'MANAGED-SESSION-003', 'MANAGED-SESSION-011'],
    source: 'src/Wanxiangshu/OpenCode/Host/SatelliteSurface.fs',
    representation: 'json',
    kind: 'resource',
  },
  {
    module: 'OpenCode/JoinResultRendererSurface.js',
    owner: 'provider-projection',
    laws: ['PROVIDER-PROJECTION-009'],
    source: 'src/Wanxiangshu/OpenCode/JoinResultRendererSurface.fs',
    representation: 'json',
    kind: 'pure',
  },
  {
    module: 'Participant/Persona/OfficeCapabilitySurface.js',
    owner: 'office-capability',
    laws: ['OFF-007', 'INTERACTION-AUTHORITY-021', 'INTERACTION-AUTHORITY-022'],
    lawOwners: {
      'INTERACTION-AUTHORITY-021': 'interaction-authority',
      'INTERACTION-AUTHORITY-022': 'interaction-authority',
    },
    source: 'src/Wanxiangshu/Participant/Persona/OfficeCapabilitySurface.fs',
    representation: 'json',
    kind: 'pure',
  },
  {
    module: 'Mission/WorkRecord/Surface.js',
    owner: 'work-record',
    laws: ['WORK-RECORD-001', 'WORK-RECORD-002', 'WORK-RECORD-003', 'WORK-RECORD-004', 'WORK-RECORD-005', 'WORK-RECORD-006', 'WORK-RECORD-007', 'WORK-RECORD-008', 'WORK-RECORD-009', 'WORK-RECORD-010', 'WORK-RECORD-011', 'WORK-RECORD-012', 'WORK-RECORD-013', 'WORK-RECORD-014', 'WORK-RECORD-015', 'WORK-RECORD-016'],
    source: 'src/Wanxiangshu/Mission/WorkRecord/Surface.fs',
    representation: 'opaque-capability',
    kind: 'resource',
  },
  {
    module: 'Context/Trace/SemanticTraceSurface.js',
    owner: 'semantic-trace',
    laws: ['SEMANTIC-TRACE-001', 'SEMANTIC-TRACE-002', 'SEMANTIC-TRACE-003', 'SEMANTIC-TRACE-004', 'SEMANTIC-TRACE-005', 'SEMANTIC-TRACE-006', 'SEMANTIC-TRACE-007', 'SEMANTIC-TRACE-008', 'SEMANTIC-TRACE-009', 'SEMANTIC-TRACE-010'],
    source: 'src/Wanxiangshu/Context/Trace/SemanticTraceSurface.fs',
    representation: 'opaque-capability',
    kind: 'resource',
  },
  {
    module: 'Execution/Delegation/Fork/Host/HostForkRunLifecycleSurface.js',
    owner: 'effect-accounting',
    laws: ['EFFECT-ACCOUNTING-002'],
    source: 'src/Wanxiangshu/Execution/Delegation/Fork/Host/HostForkRunLifecycleSurface.fs',
    representation: 'opaque-capability',
    kind: 'resource',
  },
  {
    module: 'Execution/Delegation/Fork/Host/HostForkPtySurface.js',
    owner: 'delegation',
    laws: ['MANAGED-SESSION-012'],
    lawOwners: { 'MANAGED-SESSION-012': 'managed-session-lifecycle' },
    source: 'src/Wanxiangshu/Execution/Delegation/Fork/Host/HostForkPtySurface.fs',
    representation: 'json',
    kind: 'resource',
  },
  {
    module: 'Interaction/Dispatch/DispatchSurface.js',
    owner: 'dispatch-protocol',
    laws: ['DISPATCH-PROTOCOL-002', 'DISPATCH-PROTOCOL-004', 'DISPATCH-PROTOCOL-005', 'DISPATCH-PROTOCOL-007', 'DISPATCH-PROTOCOL-009', 'DISPATCH-PROTOCOL-015', 'EFFECT-ACCOUNTING-008'],
    lawOwners: { 'EFFECT-ACCOUNTING-008': 'effect-accounting' },
    source: 'src/Wanxiangshu/Interaction/Dispatch/DispatchSurface.fs',
    representation: 'opaque-capability',
    kind: 'resource',
  },
  {
    module: 'Mission/Manager/WorkflowSurface.js',
    owner: 'relay-retirement',
    laws: ['DISPATCH-PROTOCOL-002', 'CRASH-RECONCILIATION-006'],
    lawOwners: { 'DISPATCH-PROTOCOL-002': 'dispatch-protocol', 'CRASH-RECONCILIATION-006': 'crash-reconciliation' },
    source: 'src/Wanxiangshu/Mission/Manager/WorkflowSurface.fs',
    representation: 'opaque-capability',
    kind: 'resource',
  },
  {
    module: 'Interaction/Dispatch/JoinGuardSurface.js',
    owner: 'dispatch-protocol',
    laws: ['DISPATCH-PROTOCOL-007'],
    source: 'src/Wanxiangshu/Interaction/Dispatch/JoinGuardSurface.fs',
    representation: 'opaque-capability',
    kind: 'resource',
  },
  {
    module: 'Interaction/Dispatch/RecoverySurface.js',
    owner: 'dispatch-protocol',
    laws: ['DISPATCH-PROTOCOL-007', 'DISPATCH-PROTOCOL-009', 'EFFECT-ACCOUNTING-008'],
    lawOwners: { 'EFFECT-ACCOUNTING-008': 'effect-accounting' },
    source: 'src/Wanxiangshu/Interaction/Dispatch/RecoverySurface.fs',
    representation: 'json',
    kind: 'pure',
  },
  {
    module: 'Execution/Session/LoopDetectorSurface.js',
    owner: 'degeneration-guard',
    laws: ['DG-001', 'DG-003', 'DG-004', 'DG-005', 'DG-006'],
    source: 'src/Wanxiangshu/Execution/Session/LoopDetectorSurface.fs',
    representation: 'opaque-capability',
    kind: 'resource',
  },
  {
    module: 'Change/Host/Surface.js',
    owner: 'change-integration',
    laws: ['CHGINT-002', 'CHGINT-003'],
    source: 'src/Wanxiangshu/Change/Host/Surface.fs',
    representation: 'opaque-capability',
    kind: 'resource',
  },
  {
    module: 'Foundation/ParallelSurface.js',
    owner: 'structured-workflow',
    laws: ['STRUCTURED-WORKFLOW-010'],
    source: 'src/Wanxiangshu/Foundation/ParallelSurface.fs',
    representation: 'opaque-capability',
    kind: 'pure',
  },
  {
    module: 'Foundation/FsToolkitFableCompat.js',
    owner: 'structured-workflow',
    laws: ['STRUCTURED-WORKFLOW-019'],
    source: 'src/Wanxiangshu/Foundation/FsToolkitFableCompat.fs',
    representation: 'json',
    kind: 'pure',
  },
  {
    module: 'Foundation/OutcomeSurface.js',
    owner: 'structured-workflow',
    laws: ['STRUCTURED-WORKFLOW-003'],
    source: 'src/Wanxiangshu/Foundation/OutcomeSurface.fs',
    representation: 'json',
    kind: 'pure',
  },
  {

    module: 'Enforcer/Guidance/TipSurface.js',
    owner: 'guidance-delivery',
    laws: ['GD-002', 'GD-003', 'GD-004', 'GD-005', 'GD-006', 'GD-007'],
    source: 'src/Wanxiangshu/Enforcer/Guidance/TipSurface.fs',
    representation: 'opaque-capability',
    kind: 'resource',
  },
  {
    module: 'Enforcer/Guidance/DeliverySurface.js',
    owner: 'guidance-delivery',
    laws: ['GD-001', 'GD-003', 'GD-005'],
    source: 'src/Wanxiangshu/Enforcer/Guidance/DeliverySurface.fs',
    representation: 'json',
    kind: 'pure',
  },
  {
    module: 'OpenCode/Host/PairProgramming/GuidelineSurface.js',
    owner: 'guidance-delivery',
    laws: ['GD-011'],
    source: 'src/Wanxiangshu/OpenCode/Host/PairProgramming/GuidelineSurface.fs',
    representation: 'json',
    kind: 'pure',
  },
  {
    module: 'OpenCode/Host/PairProgrammingCalibrationSurface.js',
    owner: 'guidance-delivery',
    laws: ['GD-012'],
    source: 'src/Wanxiangshu/OpenCode/Host/PairProgrammingCalibrationSurface.fs',
    representation: 'json',
    kind: 'pure',
  },
  {
    module: 'Resources/PromptSurface.js',
    owner: 'cognitive-environment',
    laws: ['COGNITIVE-ENVIRONMENT-001', 'COGNITIVE-ENVIRONMENT-003', 'COGNITIVE-ENVIRONMENT-004', 'COGNITIVE-ENVIRONMENT-005'],
    source: 'src/Wanxiangshu/Resources/PromptSurface.fs',
    representation: 'json',
    kind: 'pure',
  },
  {
    module: 'Verification/TemporalSurface.js',
    owner: 'verification-system',
    laws: ['VERIFICATION-SYSTEM-007'],
    source: 'src/Wanxiangshu/Verification/TemporalSurface.fs',
    representation: 'opaque-capability',
    kind: 'resource',
  },
  {
    module: 'Verification/JournalPortObservationSurface.js',
    owner: 'verification-system',
    laws: ['DURABLE-EVENTS-006', 'DURABLE-EVENTS-023', 'DURABLE-EVENTS-024'],
    lawOwners: { 'DURABLE-EVENTS-006': 'durable-events', 'DURABLE-EVENTS-023': 'durable-events', 'DURABLE-EVENTS-024': 'durable-events' },
    source: 'src/Wanxiangshu/Verification/JournalPortObservationSurface.fs',
    representation: 'opaque-capability',
    kind: 'resource',
  },
  {
    module: 'Mission/Relay/Surface.js',
    owner: 'relay-incumbency',
    laws: ['RELAY-001', 'RELAY-005', 'RELAY-006', 'RELAY-008', 'RELAY-009', 'RELAY-010', 'RELAY-011', 'RELAY-012', 'RETIRE-007'],
    lawOwners: { 'RETIRE-007': 'relay-retirement' },
    source: 'src/Wanxiangshu/Mission/Relay/Surface.fs',
    representation: 'json',
    kind: 'pure',
  },
  {
    module: 'Mission/Relay/Assessment/Surface.js',
    owner: 'relay-assessment',
    laws: ['ASSESS-001', 'ASSESS-007'],
    source: 'src/Wanxiangshu/Mission/Relay/Assessment/Surface.fs',
    representation: 'json',
    kind: 'pure',
  },
  {
    module: 'Mission/Planning/Surface.js',
    owner: 'planning',
    laws: [
      'planning-003',
      'planning-004',
      'planning-005',
      'planning-007',
      'planning-008',
      'planning-009',
      'planning-010',
      'planning-011',
      'planning-016',
      'planning-017',
      'planning-018',
      'planning-019',
    ],
    source: 'src/Wanxiangshu/Mission/Planning/Surface.fs',
    representation: 'json',
    kind: 'pure',
  },
  {
    module: 'OpenCode/Tools/ToolRuntimeScopeSurface.js',
    owner: 'relay-retirement',
    laws: ['RETIRE-003', 'RETIRE-009'],
    source: 'src/Wanxiangshu/OpenCode/Tools/ToolRuntimeScopeSurface.fs',
    representation: 'json',
    kind: 'resource',
  },
  {
    module: 'Mission/Relay/ProjectionSurface.js',
    owner: 'relay-context-projection',
    laws: ['PROJ-001', 'PROJ-002', 'PROJ-003', 'PROJ-004', 'PROJ-005', 'PROJ-006', 'PROJ-007', 'PROJ-008', 'PROJ-009'],
    source: 'src/Wanxiangshu/Mission/Relay/ProjectionSurface.fs',
    representation: 'opaque-capability',
    kind: 'resource',
  },
  {
    module: 'OpenCode/Host/ProviderFailurePresentation.js',
    owner: 'host-provider-failure-ownership',
    laws: ['HOSTFAIL-003', 'HOSTFAIL-004', 'HOSTFAIL-006'],
    source: 'src/Wanxiangshu/OpenCode/Host/ProviderFailurePresentation.fs',
    representation: 'json',
    kind: 'pure',
  },
]

/** Flat module-path allowlist derived from the manifest (scanner regex input). */
export const SURFACE_MODULES = SURFACE_MANIFEST.map((entry) => entry.module)

const SURFACE_ALT = SURFACE_MODULES.map((m) => m.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')).join('|')

const A_DEEP_IMPORT = new RegExp(
  `(?:from\\s*|import\\s*\\(\\s*|import\\s+)['"][^'"]*dist/(?!fable_modules)(?!(?:${SURFACE_ALT})['"])[^'"]+\\.js['"]`,
)
// Dynamic template import: `new URL(`...dist/${...}.js`, ...)` bypasses the
// static-import regex. A template literal with interpolation that contains
// `dist/` and ends in `.js` is a deep import — the variable means the scanner
// cannot prove it loads only registered surfaces, so it is always debt.
const A_TEMPLATE_IMPORT = /new URL\(\s*[`'"][^`'"]*dist\/[^`'"]*\$\{[^}]+\}[^`'"]*\.js[`'"]/
const B_EXPORT_DISCOVERY = /Object\.(?:keys|entries|values)\(\s*([A-Za-z_$][\w$]*)/
const B_MANGLED_LOOKUP = /(?:\.startsWith|\.endsWith)\(\s*['"`][^'"`]*(?:__|_[A-Z])/

const isEnvironmentKeys = (node) => node?.type === 'CallExpression'
  && node.callee?.object?.name === 'Object' && node.callee?.property?.name === 'keys'
  && node.arguments[0]?.object?.name === 'process' && node.arguments[0]?.property?.name === 'env'

const mangledLookupLines = (source, syntax) => {
  const lines = new Set()
  const visit = (node, environmentNames = new Set()) => {
    if (!node || typeof node !== 'object') return
    if (node.type === 'ForOfStatement' && isEnvironmentKeys(node.right)) {
      const name = node.left.declarations?.[0]?.id?.name
      visit(node.body, new Set([...environmentNames, name]))
      return
    }
    if (node.type === 'CallExpression') {
      const callee = node.callee
      if (callee?.property?.name === 'filter' && isEnvironmentKeys(callee.object)) {
        const callback = node.arguments[0]
        visit(callback?.body, new Set([...environmentNames, callback?.params?.[0]?.name]))
        return
      }
      if (['startsWith', 'endsWith'].includes(callee?.property?.name)
        && typeof node.arguments[0]?.value === 'string' && /__|_[A-Z]/.test(node.arguments[0].value)
        && !(callee.object?.type === 'Identifier' && environmentNames.has(callee.object.name))) {
        lines.add(node.loc.start.line)
      }
    }
    for (const value of Object.values(node)) {
      if (Array.isArray(value)) value.forEach((child) => visit(child, environmentNames))
      else if (value && typeof value === 'object') visit(value, environmentNames)
    }
  }
  if (syntax) visit(syntax)
  else {
    source.split('\n').forEach((line, index) => { if (B_MANGLED_LOOKUP.test(line)) lines.add(index + 1) })
  }
  return lines
}

const syntaxChildren = node => Object.values(node).flatMap(value =>
  Array.isArray(value) ? value.filter(child => child?.type) : value?.type ? [value] : [])

const bindingNames = pattern => {
  if (!pattern) return []
  if (pattern.type === 'Identifier') return [pattern.name]
  if (pattern.type === 'Property') return bindingNames(pattern.value)
  if (pattern.type === 'AssignmentPattern') return bindingNames(pattern.left)
  return syntaxChildren(pattern).flatMap(bindingNames)
}

const receiverName = node => {
  if (node?.type === 'Identifier') return node.name
  if (node?.type === 'MemberExpression') return receiverName(node.object)
  return null
}

// A literal container ceases to be a proof once it can be changed or escape
// through an alias/call. Name collisions conservatively retain the rejection.
const escapedDataNames = syntax => {
  const names = new Set()
  const mark = node => {
    const name = receiverName(node)
    if (name) names.add(name)
  }
  const visit = node => {
    if (node.type === 'AssignmentExpression') {
      mark(node.left)
      mark(node.right)
    }
    if (node.type === 'UpdateExpression') mark(node.argument)
    if (node.type === 'VariableDeclarator' && (node.init?.type === 'Identifier' || node.init?.computed)) mark(node.init)
    if (node.type === 'ReturnStatement' && node.argument?.type === 'Identifier') mark(node.argument)
    if (node.type === 'ArrowFunctionExpression') mark(node.body)
    if (node.type === 'Property') mark(node.value)
    if (node.type === 'ArrayExpression') node.elements.forEach(mark)
    if (node.type === 'CallExpression' || node.type === 'NewExpression') {
      if (node.callee.type === 'MemberExpression') mark(node.callee.object)
      node.arguments.forEach(argument => { if (argument.type === 'Identifier') mark(argument) })
    }
    if (node.type === 'ExportSpecifier') mark(node.local)
    syntaxChildren(node).forEach(visit)
  }
  visit(syntax)
  return names
}

const isLiteralData = node => {
  if (!node) return false
  if (node.type === 'Literal') return true
  if (node.type === 'ArrayExpression') return node.elements.every(isLiteralData)
  if (node.type !== 'ObjectExpression') return false
  return node.properties.every(property => property.type === 'Property'
    && property.kind === 'init' && !property.method && !property.computed
    && isLiteralData(property.value))
}

const fieldsAccessLines = (source, syntax) => {
  const lines = new Set()
  if (!syntax) {
    source.split('\n').forEach((line, index) => { if (/\.fields\b/.test(line)) lines.add(index + 1) })
    return lines
  }
  const escaped = escapedDataNames(syntax)
  const values = (node, bindings) => node?.type === 'Identifier'
    ? escaped.has(node.name) ? [] : bindings.get(node.name) ?? []
    : node ? [node] : []
  const nativeObject = (node, bindings) => {
    const candidates = values(node, bindings)
    return candidates.length > 0 && candidates.every(value => value.type === 'ObjectExpression' && isLiteralData(value))
  }
  const bindUnknown = (bindings, pattern) => bindingNames(pattern).forEach(name => bindings.set(name, []))
  const visit = (node, bindings) => {
    if (node.type === 'Program' || node.type === 'BlockStatement') {
      const local = new Map(bindings)
      for (const statement of node.body) {
        if (statement.type === 'VariableDeclaration') statement.declarations.forEach(declaration => bindUnknown(local, declaration.id))
        if (statement.type === 'FunctionDeclaration' || statement.type === 'ClassDeclaration') bindUnknown(local, statement.id)
      }
      node.body.forEach(statement => visit(statement, local))
      return
    }
    if (['FunctionDeclaration', 'FunctionExpression', 'ArrowFunctionExpression'].includes(node.type)) {
      const local = new Map(bindings)
      node.params.forEach(parameter => bindUnknown(local, parameter))
      bindUnknown(local, node.id)
      syntaxChildren(node).forEach(child => visit(child, local))
      return
    }
    if (node.type === 'VariableDeclaration') {
      for (const declaration of node.declarations) {
        visit(declaration.id, bindings)
        if (declaration.init) visit(declaration.init, bindings)
        bindUnknown(bindings, declaration.id)
        if (node.kind === 'const' && declaration.id.type === 'Identifier' && isLiteralData(declaration.init)) {
          bindings.set(declaration.id.name, [declaration.init])
        }
      }
      return
    }
    if (node.type === 'ForOfStatement') {
      visit(node.right, bindings)
      const local = new Map(bindings)
      const declaration = node.left.declarations?.[0]
      bindUnknown(local, declaration?.id ?? node.left)
      visit(declaration?.id ?? node.left, local)
      const candidates = values(node.right, bindings)
      const array = candidates.length === 1 ? candidates[0] : null
      if (node.left.kind === 'const' && declaration?.id.type === 'Identifier'
        && array?.type === 'ArrayExpression' && isLiteralData(array)) {
        local.set(declaration.id.name, array.elements)
      }
      visit(node.body, local)
      return
    }
    if (node.type === 'CatchClause') {
      const local = new Map(bindings)
      bindUnknown(local, node.param)
      visit(node.body, local)
      return
    }
    if (node.type === 'MemberExpression'
      && (node.computed ? node.property.value === 'fields' : node.property.name === 'fields')
      && !nativeObject(node.object, bindings)) lines.add(node.loc.start.line)
    syntaxChildren(node).forEach(child => visit(child, bindings))
  }
  visit(syntax, new Map())
  return lines
}

const C1_DU_SHAPE = /\.cases\(\)|\.tag\b/
const C2_FSHARP = /\bFSharp(?:List|Map|Set|Option|Result)\b/
const C3_FABLE_MODULES = /fable_modules/
// Ordinary JavaScript `.bind(...)` is not the legacy Fable helper; bare calls remain forbidden.
const D_HELPERS = /(?<![.$])\b(?:member|bind|fableInstanceMethod|prod|toList|caseOf|payloadOf|resultOf|unwrapOption)\(/

const RULES = [
  ['deep-dist-import', A_DEEP_IMPORT],
  ['template-dist-import', A_TEMPLATE_IMPORT],
  ['export-discovery', B_EXPORT_DISCOVERY],
  ['mangled-lookup', B_MANGLED_LOOKUP],
  ['du-shape', C1_DU_SHAPE],
  ['fsharp-type', C2_FSHARP],
  ['fable-modules', C3_FABLE_MODULES],
  ['interop-helper', D_HELPERS],
]

const moduleBindingNames = (source) => {
  const names = new Set(['mod', 'module', 'hostModule', 'productionModule'])
  const patterns = [
    /\b(?:const|let|var)\s+([A-Za-z_$][\w$]*)\s*=\s*(?:await\s+)?(?:import|prod|bind)\s*\(/g,
    /\bimport\s+\*\s+as\s+([A-Za-z_$][\w$]*)\b/g,
  ]
  for (const pattern of patterns) {
    pattern.lastIndex = 0
    let match
    while ((match = pattern.exec(source)) !== null) names.add(match[1])
  }
  return names
}

const isModuleDiscovery = (text, moduleNames) => {
  const keys = B_EXPORT_DISCOVERY.exec(text)
  B_EXPORT_DISCOVERY.lastIndex = 0
  if (keys && moduleNames.has(keys[1])) return true
  return false
}

/** Scan one file; return [{ line, rule, text }]. */
export const scanFile = (absPath, relPath) => {
  const source = readFileSync(absPath, 'utf8')
  const lines = source.split('\n')
  const moduleNames = moduleBindingNames(source)
  let syntax
  try {
    syntax = parse(source, { ecmaVersion: 'latest', sourceType: 'module', locations: true })
  } catch {
    syntax = null
  }
  const mangledLines = mangledLookupLines(source, syntax)
  const fieldsLines = fieldsAccessLines(source, syntax)
  const hits = []
  for (let i = 0; i < lines.length; i++) {
    const text = lines[i]
    for (const [rule, re] of RULES) {
      if (rule === 'export-discovery') {
        if (isModuleDiscovery(text, moduleNames)) hits.push({ file: relPath, line: i + 1, rule, text: text.trim() })
      } else if (rule === 'mangled-lookup' ? mangledLines.has(i + 1)
        : rule === 'du-shape' ? fieldsLines.has(i + 1) || re.test(text) : re.test(text)) {
        hits.push({ file: relPath, line: i + 1, rule, text: text.trim() })
      }
    }
  }
  return hits
}

const IMPORT_SPECIFIER = /(?:\bfrom\s*|\bimport\s*\(\s*|\bimport\s+)['"]([^'"]+)['"]/g

/** Resolve relative local imports from semantic tests into the scanned zone.
 *
 * Traverses transitively from `.test.mjs` roots through all semantic-zone
 * files (support, fixtures, helpers): a support→support edge that carries
 * debt is just as much a test dependency as a direct test→support edge.
 */
export const semanticImportEdges = (root = REQUIREMENTS_ROOT) => {
  const files = semanticTestFiles(root)
  const known = new Set(files.map((file) => resolve(file)))
  const edges = []
  const visited = new Set()
  const queue = files.filter((file) => file.endsWith('.test.mjs'))
  while (queue.length > 0) {
    const importer = queue.shift()
    const importerKey = resolve(importer)
    if (visited.has(importerKey)) continue
    visited.add(importerKey)
    const source = readFileSync(importer, 'utf8')
    IMPORT_SPECIFIER.lastIndex = 0
    let match
    while ((match = IMPORT_SPECIFIER.exec(source)) !== null) {
      if (!match[1].startsWith('.')) continue
      const target = resolve(dirname(importer), match[1])
      if (known.has(target)) {
        edges.push({ importer, target })
        if (!visited.has(target)) queue.push(target)
      }
    }
  }
  return edges
}

/**
 * Semantic-test zone files under requirements: every executable .mjs or .js
 * under a package's tests directory — *.test.*, support files, fixtures,
 * helpers, *-contract.*, e2e, and integration.
 *
 * TASK.md §7/§21: forbidden knowledge moved from a test file into test
 * support does not reduce debt; the whole semantic-test dependency zone is
 * scanned. The transition facade (support/domain.mjs and its sublayers) and
 * package-local contract adapters remain debt only while they are being
 * removed; neither path is a quarantine.
 */
export const semanticTestFiles = (root = REQUIREMENTS_ROOT) =>
  walk(root, ['.mjs', '.js']).filter((abs) => {
    const segments = relative(root, abs).replace(/\\/g, '/').split('/')
    const testsIndex = segments.indexOf('tests')
    return testsIndex > 0
  })

/** Full inventory: { <rel-file>: [ {line, rule, text}, ... ] } minus allowlist. */
export const scanAll = (root = REQUIREMENTS_ROOT) => {
  const out = {}
  for (const abs of semanticTestFiles(root)) {
    const rel = relative(process.cwd(), abs).replace(/\\/g, '/')
    if (BUILD_VERIFICATION_FILES.has(rel)) continue
    if (HOST_PHYSICAL_CANARY_FILES.has(rel)) continue
    const hits = scanFile(abs, rel)
    if (hits.length > 0) out[rel] = hits
  }
  return out
}
