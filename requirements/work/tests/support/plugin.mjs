import { withExecutablePlugin, acceptAuthorityRoot } from '../../../verification-system/tests/support/plugin-fixture.mjs'

const lifecycleSurface = await import('../../../../dist/Mission/Lifecycle/Surface.js')
const journalSurface = await import('../../../../dist/Persistence/Journal/Surface.js')

process.env.WANXIANGSHU_PROVIDER_LANGUAGE = 'en'

// Keyed findings: findings is an array of { key, value }. A null value deletes the key.
export const findings = (grade, key = 'target-gap') => {
  if (grade === 'REVISE') {
    return { findings: [{ key, value: 'close the remaining gap' }] }
  }
  if (grade === 'CLEAR') {
    return { findings: [{ key, value: null }] }
  }
  return { findings: [] }
}
export const scores = findings
// ModelRouting is a process singleton: a test that supersedes a physical
// message keeps it superseded for later tests, so isolated callers pass their own session.
export const withReview = async (body, { session = 'review-manager' } = {}) => withExecutablePlugin(async (hooks, directory, created, runtime) => {
  runtime.client.tui = runtime.client.tui ?? { showToast: async () => ({}) }
  await acceptAuthorityRoot(runtime, session, 'manager', 'user-root')
  await hooks['chat.message']({sessionID: session, messageID: `user-kickoff-${directory}`, agent: 'manager'}, {
    message: {id: `user-kickoff-${directory}`, role: 'user', agent: 'manager', model: {providerID: 'provider', modelID: 'manager-model'}},
    parts: [{type: 'text', text: 'Initial request before clarification.'}],
  })
  const commonDir = await journalSurface.JournalSurface_initializeStateRoot(directory)
  const surface = lifecycleSurface.LifecycleSurface ?? lifecycleSurface
  const opened = await surface.submitReview(
    commonDir,
    session,
    'open-review',
    'Manager',
    'run-open',
    'call-open',
    [{ key: 'target-gap', value: 'open' }],
    null,
    '',
  )
  if (opened.kind !== 'Reviewed') {
    throw new Error(`manager review did not open: ${opened.kind ?? ''} ${opened.reason ?? ''}`)
  }
  await hooks.tool['todowrite-for-manager'].execute(
    {
      name: 'kickoff-kept-review',
      program: 'class Js extends JsProgram { async run() { return await this.review([{ key: "target-gap", value: "open" }]); } }',
      intent: 'Keep the opened obligation and wait for this review to settle.',
    },
    { sessionID: session, callID: 'kickoff-call', messageID: 'kickoff-run', agent: 'manager' },
  )
  await hooks['chat.message']({sessionID: session, messageID: 'user-root', agent: 'manager'}, {
    message: {id: 'user-root', role: 'user', agent: 'manager', model: {providerID: 'provider', modelID: 'manager-model'}},
    parts: [{type: 'text', text: 'Deliver the requested behavior and verification.'}],
  })
  const userRootMsg = {info: {id: 'user-root', role: 'user', sessionID: session, time: {created: 1}, model: {providerID: 'provider', modelID: 'manager-model'}}, parts: [{type: 'text', text: 'Deliver the requested behavior and verification.'}]}
  runtime.pushHostMessage(session, userRootMsg)
  const kickoffRun = {info: {id: 'kickoff-answered-run', role: 'assistant', sessionID: session, parentID: 'user-root', time: {created: 1}}, parts: []}
  runtime.pushHostMessage(session, kickoffRun)
  await hooks['experimental.chat.messages.transform']({sessionID: session}, {messages: [userRootMsg]})
  kickoffRun.info.time = { ...kickoffRun.info.time, completed: 1 }
  let createdAt = 1
  const execute = async (input, {call = 'review-call', run = 'review-run', before = 'I independently inspected the current workspace.', after = 'after review'} = {}) => {
    runtime.pushHostMessage(session, {
      info: {id: run, role: 'assistant', sessionID: session, parentID: 'user-root', time: {created: ++createdAt}},
      parts: [
        {type: 'reasoning', text: 'private reasoning is not public evidence'},
        {type: 'text', text: before},
        {type: 'tool', tool: 'todowrite-for-manager', callID: call, state: {status: 'pending', input: {}}},
        {type: 'text', text: after},
      ],
    })
    const findingsArray = Array.isArray(input) ? input : (input?.findings ?? [])
    const program = `class Js extends JsProgram {
      async run() {
        return await this.review(${JSON.stringify(findingsArray)});
      }
    }`
    const result = await hooks.tool['todowrite-for-manager'].execute(
      { name: 'review-' + call, program, intent: 'Wait for this review to settle and preserve its original result.' },
      { sessionID: session, callID: call, messageID: run, agent: 'manager' },
    )
    // The review run is finished once its tool returns. A later provider request
    // of the same user message must not see it as a second unsealed assistant.
    const pushed = runtime.messages.find(message => (message.info?.id ?? message.id) === run)
    if (pushed?.info) pushed.info.time = { ...pushed.info.time, completed: createdAt }
    return result
  }
  await body({hooks, directory, runtime, session, execute, created})
})
