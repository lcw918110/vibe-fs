import assert from 'node:assert/strict'
import { integrationTest } from '../../verification-system/tests/support/tier-gate.mjs'
import { acceptAuthorityRoot, withExecutablePlugin, withPlugin } from '../../verification-system/tests/support/plugin-fixture.mjs'

integrationTest('WHAT[action-affordance-014] the registered assume schema exposes only assumption', async () => {
  await withPlugin(async (hooks) => {
    const args = hooks.tool.assume.args
    assert.deepEqual(Object.keys(args).sort(), ['assumption', 'scope'])
    assert.equal(args.assumption.safeParse('Use the smaller state machine.').success, true)
    assert.equal(args.assumption.safeParse(undefined).success, false)
    assert.equal(Object.prototype.hasOwnProperty.call(args, 'update'), false)
    assert.equal(Object.prototype.hasOwnProperty.call(args, 'todos'), false)
  })
})

integrationTest('WHAT[action-affordance-014] assume commits without echoing the assumption or writing a canvas', async () => {
  await withExecutablePlugin(async (hooks, _directory, _createdIds, runtime) => {
    const sessionID = 'assume-independent'
    await acceptAuthorityRoot(runtime, sessionID, 'engineer')

    const assumption = 'Use the smaller state machine and remove the canvas.'
    const result = await hooks.tool.assume.execute(
      { assumption },
      { sessionID, agent: 'engineer', callID: 'assume-1', messageID: 'message-assume-1' },
    )

    // The fixed prompt is rendered in the current global language; the alternation pins the
    // whole string instead of merely checking that some answer came back.
    assert.match(
      result,
      /^已笃定，不再因为没有信息增量的犹豫反复改判。$|^Committed\. Do not reopen the judgment merely because of hesitation without new information\.$/,
    )
    assert.equal(result.includes(assumption), false, 'the committed prompt must not echo the assumption')
  })
})
