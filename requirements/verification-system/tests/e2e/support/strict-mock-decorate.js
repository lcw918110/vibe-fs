/**
 * strict-mock-decorate.js — Args decoration (follow-tdd-and-kolmogorov-principles /
 * impossible-via-other-tools / not-suitable-via-continue-tool) used by StrictMockProvider.
 */

const DEFAULT_WARN_TDD = 'i-am-sure-i-have-followed-tdd-and-kolmogorov-principles-and-kept-todo-updated';
const DEFAULT_WARN = 'MUST acknowledge that this task cannot be done with other tools and only run tests when static analysis cannot handle it';
const DEFAULT_WARN_REUSE = 'MUST acknowledge that this task cannot be completed using the continue tool';

const WARN_TDD_KEY = 'follow-tdd-and-kolmogorov-principles';
const WARN_KEY = 'impossible-via-other-tools';
const WARN_REUSE_KEY = 'not-suitable-via-continue-tool';

/**
 * Scope annotation (common law «Scope», `wanxiangli/scope-retention`).
 *
 * Production refuses any tool call whose args carry no `scope`, so a mock that
 * answers without one is not playing a model — it is declining to play at all.
 * The first real annotation of a session must be `resetScope`; later turns pay
 * one `keepLast`. A turn carries exactly one real annotation, so every tool call
 * after the first in the same response is `meToo`.
 *
 * A scenario that wants to exercise the annotation itself owns the field: an
 * args object with an own `scope` key is passed through untouched.
 */
const SCOPE_RESET_GOAL = 'Continue the current assignment.'
const SCOPE_RESET_BUDGET = 48

/**
 * Exempt tools (AXIOM 5, `wanxiangli/scope-retention`): `keepLast`/`meToo` on them
 * neither spends retention nor decides the turn. Handing one `resetScope` would
 * spend the session's one initialization on a lifecycle-neutral call, so the next
 * real tool finds "scope not initialized" again.
 */
const SCOPE_EXEMPT_TOOLS = new Set(['negotiate', 'askPreviousScope', 'askPrevious', 'deliver'])

export function decorateScopeArgs(args, retention, toolName, position = 0) {
  if (args == null || typeof args !== 'object') return args
  if (Object.prototype.hasOwnProperty.call(args, 'scope')) return args
  if (position > 0) {
    args.scope = { kind: 'meToo' }
    return args
  }
  if (SCOPE_EXEMPT_TOOLS.has(toolName)) {
    args.scope = { kind: 'keepLast' }
    return args
  }
  if (retention.opened.has(retention.sessionId)) {
    args.scope = { kind: 'keepLast' }
    return args
  }
  retention.opened.add(retention.sessionId)
  args.scope = {
    kind: 'resetScope',
    newScopeGoal: SCOPE_RESET_GOAL,
    newScopeRetentionBudget: SCOPE_RESET_BUDGET,
  }
  return args
}

function setOrDelete(args, key, defaultValue) {
  if (args[key] === null) {
    delete args[key];
    return;
  }
  if (!(key in args)) args[key] = defaultValue;
}

export function decorateLegacyArgs(args) {
  setOrDelete(args, WARN_TDD_KEY, DEFAULT_WARN_TDD);
  setOrDelete(args, WARN_KEY, DEFAULT_WARN);
  setOrDelete(args, WARN_REUSE_KEY, DEFAULT_WARN_REUSE);
}
