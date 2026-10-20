// Wanxiangshu model scheduler. Edit this file freely.
// `running` is a multiset of active { model, reasoning } leases.
// `previous` is the last successful physical execution target for this session,
// or null for a new conversation. It is a preference hint, not occupancy.
// Return a target to acquire it, or null to wait for an occupancy change.
// Public roles: manager, orchestrator, engineer, devops, plan, advisor.
// bookkeeper is an internal runtime mechanism, not a dispatch target.
// A model choice does not change role authority. Retired and unknown roles fail closed.
//
// Migration note for an existing configuration: this scheduler speaks
// protocol 2 (route(role, running, previous)). This file is the reference shape; an
// existing user configuration is never overwritten by the runtime.

// Stable contract version of the scheduler ABI. It is not an enable switch.
export const routingProtocol = 2

// Provider-level concurrency limits (maximum concurrent active leases per provider).
const PROVIDER_LIMITS = {
  'ollama-cloud': 16,
  'opencode-go': 8,
  'stepfun': 8,
  'cursor': 4,
  'neuralwatt': 4,
}

const DEFAULT_LIMIT = 4

const providerOf = (model) => model.slice(0, model.indexOf('/'))

// Track providers marked as failed until process restart.
const failedProviders = new Set()

export const markProviderFailed = (provider) => {
  if (provider) failedProviders.add(provider)
}

export const clearFailedProviders = () => {
  failedProviders.clear()
}

export const providerCapacity = (provider) => {
  if (failedProviders.has(provider)) return 0
  return PROVIDER_LIMITS[provider] ?? DEFAULT_LIMIT
}

const isAvailable = (running, model) => {
  const provider = providerOf(model)
  const limit = providerCapacity(provider)
  if (limit <= 0) return false
  const count = running.filter((item) => providerOf(item.model) === provider).length
  return count < limit
}

const targetOf = ([model, reasoning]) => ({ model, reasoning })

const pick = (running, previous, candidates) => {
  if (previous) {
    const preferred = candidates.find(
      ([model, reasoning]) => model === previous.model && reasoning === previous.reasoning,
    )
    if (preferred && isAvailable(running, preferred[0])) return targetOf(preferred)
  }

  for (const candidate of candidates) {
    if (isAvailable(running, candidate[0])) return targetOf(candidate)
  }
  return null
}

const CHEAP_A = [
  ['ollama-cloud/gemma4:31b', 'none'],
  ['opencode-go/deepseek-v4-flash', 'none'],
]

const CHEAP_B = [
  ['opencode-go/deepseek-v4-flash', 'none'],
]

const FLASH = [
  ['stepfun/step-3.5-flash-2603', 'none'],
]

const STANDARD = [
  ['opencode-go/deepseek-v4-flash', 'low'],
]

const PREMIUM = [
  ['cursor/cursor-grok-4.6-xhigh', 'xhigh'],
  ['neuralwatt/glm-5.2-flex', 'high'],
]

const ENGINEER_POOL = [
  ...PREMIUM,
  ...STANDARD,
  ...FLASH,
]

const MANAGER_POOL = [
  ...PREMIUM,
  ...STANDARD,
]

const ORCHESTRATOR_POOL = [
  ...PREMIUM,
  ...STANDARD,
]

const DEVOPS_POOL = [
  ...PREMIUM,
  ...STANDARD,
]

const BOOKKEEPER_POOL = [
  ...FLASH,
  ...STANDARD,
]

const pools = new Map([
  ['engineer', ENGINEER_POOL],
  ['manager', MANAGER_POOL],
  ['orchestrator', ORCHESTRATOR_POOL],
  ['devops', DEVOPS_POOL],
  ['bookkeeper', BOOKKEEPER_POOL],
])

export const hasTheoreticalCapacity = (role) => {
  const candidates = pools.get(role)
  if (!candidates || candidates.length === 0) return false
  return candidates.some(([model]) => {
    const provider = providerOf(model)
    return providerCapacity(provider) > 0
  })
}

export default function route(role, running, previous) {
  // The fixed DevOps target preference belongs to the owner execution only.
  if (role === 'devops' && previous) {
    if (isAvailable(running, previous.model)) {
      return previous
    }
  }

  const candidates = pools.get(role)
  if (!candidates) return null
  return pick(running, previous, candidates)
}
