import assert from 'node:assert/strict'
import test from 'node:test'
import { execFileSync, spawnSync } from 'node:child_process'
import { fileURLToPath } from 'node:url'
import { mkdtempSync, rmSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import * as forkTool from '../../../dist/Execution/Delegation/Fork/OpenCode/ToolSurface.js'
import { injectAcceptedAssessment } from '../../verification-system/tests/support/plugin-fixture.mjs'
import { integrationTest } from '../../verification-system/tests/support/tier-gate.mjs'

for (const kind of ['Failed', 'Aborted']) {
  test('WHAT[delegation-027] root affinity also rejects old ' + kind + ' without a prepared handoff', async () => {
    const owner = 'unprepared-affinity-' + kind
    await withForkRuntime(owner, async runtime => {
      const started = forkTool.startUnprepared(runtime, owner, 'WORK-WITHOUT-HANDOFF')
      await forkTool.awaitPromptCount(runtime, 1)
      assert.equal(forkTool.acceptPrompt(runtime, 0), true)
      assert.equal((await started).ok, true)
      const before = forkTool.workSnapshot(runtime, owner)
      assert.equal(before.length, 1)
      await forkTool.emitStopForRoot(runtime, owner, 'another-work-root', kind)
      assert.deepEqual(forkTool.workSnapshot(runtime, owner), before)
      assert.equal(await forkTool.settle(runtime, owner, 'CURRENT-WORK-ANSWER', 'current-provider'), true)
      const result = await forkTool.executeJoin(runtime, owner)
      assert.match(result, /CURRENT-WORK-ANSWER/)
      assert.doesNotMatch(result, /old work stop/)
    })
  })
}
import { toolModule, withForkRuntime } from './support/fork-runtime.mjs'

test('WHAT[delegation-027] busy guidance preserves the original work and completion before an idle resume starts new work', async () => {
  const owner = 'owner-busy-road'
  await withForkRuntime(owner, async (runtime, directory) => {
    const first = forkTool.executeManagerFork(runtime, toolModule, owner, 'engineer', 'Ada', 'FIRST')
    await forkTool.awaitPromptCount(runtime, 1)
    assert.equal(forkTool.acceptPrompt(runtime, 0), true)
    assert.match(await first, /Ada/)
    const original = forkTool.workSnapshot(runtime, owner)
    const listeners = forkTool.terminalListenerCount(runtime)
    const completeOriginal = await forkTool.prepareTerminalDelivery(runtime, owner, 'FIRST-DONE', 'first-run')
    forkTool.acceptNextPrompt(runtime)
    const guided = await forkTool.executeManagerResume(runtime, toolModule, owner, '', 'Ada', 'GUIDANCE-WHILE-BUSY')
    assert.equal(forkTool.promptCount(runtime), 2, guided)
    assert.match(forkTool.prompt(runtime, 1), /GUIDANCE-WHILE-BUSY/)
    assert.deepEqual(forkTool.workSnapshot(runtime, owner), original)
    assert.deepEqual(await forkTool.coldWorkSnapshot(directory, owner), original)
    assert.equal(forkTool.terminalListenerCount(runtime), listeners)
    assert.equal(forkTool.abortCount(runtime), 0)
    assert.equal(forkTool.durableLifecycleByname(runtime, owner, 'Ada'), 'Active')
    await completeOriginal()
    assert.equal(forkTool.workSnapshot(runtime, owner)[0].lifecycle, 'CompletedAwaitingJoin')
    const next = forkTool.executeManagerResume(runtime, toolModule, owner, '', 'Ada', 'SECOND-AFTER-COMPLETION')
    await forkTool.awaitPromptCount(runtime, 3)
    assert.equal(forkTool.acceptPrompt(runtime, 2), true)
    await next
    assert.equal(forkTool.childCount(runtime), 1)
    const works = forkTool.workSnapshot(runtime, owner)
    assert.equal(works.length, 2)
    assert.notEqual(works[1].root, original[0].root)
    assert.equal(await forkTool.settle(runtime, owner, 'SECOND-DONE', 'second-run'), true)
    const joined = await forkTool.executeJoin(runtime, owner)
    assert.match(joined, /FIRST-DONE/)
    assert.match(joined, /SECOND-DONE/)
  })
})

for (const byname of ['Ada', 'devops']) {
for (const replacement of ['active', 'finished', 'successor']) {
  test(`WHAT[delegation-027] prepared busy guidance for ${byname} stays bound to the original ${replacement} work`, async () => {
    const owner = `busy-guidance-${byname}-${replacement}`
    const directory = mkdtempSync(join(tmpdir(), 'wxs-busy-guidance-'))
    const preparing = Promise.withResolvers()
    const release = Promise.withResolvers()
    let attachmentSession
    // The same delivered-view read port is exercised by settle and by the
    // attachment preparation; only the latter may block here, otherwise the
    // fixture deadlocks before guidance even starts.
    let armAttachmentBlock = false
    const runtime = await forkTool.createRuntimeWithViewRead(directory, [{ sessionId: owner, agent: 'manager' }], async session => {
      if (armAttachmentBlock && session === attachmentSession) {
        preparing.resolve()
        await release.promise
      }
    })
    try {
      forkTool.acceptNextPrompt(runtime)
      await forkTool.executeManagerFork(runtime, toolModule, owner, 'engineer', 'Source', 'ATTACHMENT-SOURCE')
      attachmentSession = forkTool.child(runtime)
      assert.equal(await forkTool.settle(runtime, owner, 'SOURCE-DONE', 'source-provider'), true)
      forkTool.acceptNextPrompt(runtime)
      if (byname === 'devops') {
        execFileSync('git', ['init', '--quiet', directory])
        runtime.directory = directory
        await injectAcceptedAssessment(runtime, owner)
        assert.match(await forkTool.executeManagerResume(runtime, toolModule, owner, '', byname, 'ORIGINAL-WORK'), /carries this charge now/)
      } else {
        assert.match(await forkTool.executeManagerFork(runtime, toolModule, owner, 'engineer', byname, 'ORIGINAL-WORK'), /carries this charge now/)
      }
      const original = forkTool.workSnapshot(runtime, owner)
      const listeners = forkTool.terminalListenerCount(runtime)
      const completeOriginal = await forkTool.prepareTerminalDelivery(runtime, owner, 'ORIGINAL-DONE', 'original-provider')
      armAttachmentBlock = true
      const guidance = forkTool.executeManagerResumeWithAttachment(runtime, toolModule, owner, byname, 'ORIGINAL-GUIDANCE', 'Source')
      await preparing.promise
      assert.equal(forkTool.promptCount(runtime), 1, 'attachment preparation has not sent guidance')
      if (replacement !== 'active') {
        await completeOriginal()
        assert.equal(forkTool.workSnapshot(runtime, owner).find(work => work.byname === byname).lifecycle, 'CompletedAwaitingJoin')
      }
      if (replacement === 'successor') {
        forkTool.acceptNextPrompt(runtime)
        await forkTool.executeManagerResume(runtime, toolModule, owner, '', byname, 'SUCCESSOR-WORK')
      }
      const beforeRelease = forkTool.workSnapshot(runtime, owner)
      const countBeforeRelease = forkTool.promptCount(runtime)
      forkTool.acceptNextPrompt(runtime)
      release.resolve()
      const result = await guidance
      if (replacement === 'active') {
        assert.match(result, /Guidance for .* has been appended/)
        assert.equal(forkTool.promptCount(runtime), countBeforeRelease + 1)
        assert.match(forkTool.prompt(runtime, countBeforeRelease), /ORIGINAL-GUIDANCE/)
        assert.deepEqual(forkTool.workSnapshot(runtime, owner), original)
        assert.equal(forkTool.terminalListenerCount(runtime), listeners)
        forkTool.acceptNextPrompt(runtime)
        await forkTool.executeManagerResumeWithAttachment(runtime, toolModule, owner, byname, 'ORIGINAL-GUIDANCE', 'Source')
        assert.equal(forkTool.promptCount(runtime), countBeforeRelease + 2, 'identical text is a real independent guidance send')
        assert.equal(forkTool.prompt(runtime, countBeforeRelease + 1), forkTool.prompt(runtime, countBeforeRelease))
        assert.notEqual(forkTool.promptEvidence(runtime, countBeforeRelease + 1).promptKey, forkTool.promptEvidence(runtime, countBeforeRelease).promptKey)
        assert.deepEqual(forkTool.workSnapshot(runtime, owner), original)
        await completeOriginal()
      } else {
        assert.equal(result, '# The charge could not be placed.\n')
        assert.equal(forkTool.promptCount(runtime), countBeforeRelease, 'stale guidance never reaches Host')
        assert.deepEqual(forkTool.workSnapshot(runtime, owner), beforeRelease)
        if (replacement === 'successor') {
          assert.doesNotMatch(forkTool.prompt(runtime, countBeforeRelease - 1), /ORIGINAL-GUIDANCE/)
          const completeSuccessor = await forkTool.prepareTerminalDelivery(runtime, owner, 'SUCCESSOR-DONE', 'successor-provider')
          await completeSuccessor()
        }
      }
      assert.equal(forkTool.abortCount(runtime), 0)
      assert.deepEqual(await forkTool.coldWorkSnapshot(directory, owner), forkTool.workSnapshot(runtime, owner))
      const joined = await forkTool.executeJoin(runtime, owner)
      assert.match(joined, /ORIGINAL-DONE/)
      if (replacement === 'successor') assert.match(joined, /SUCCESSOR-DONE/)
    } finally {
      release.resolve()
      forkTool.disposeRuntime(runtime)
      rmSync(directory, { recursive: true, force: true })
    }
  })
}
}

for (const phase of ['GUIDANCE', 'GUIDANCE_STOP']) {
integrationTest(`WHAT[delegation-027] installed Host releases Manager Join and appends guidance after the child ${phase === 'GUIDANCE_STOP' ? 'final output' : 'tool step'}`, () => {
  const runner = fileURLToPath(new URL('../../host-boundary/tests/support/run-user-input-canary.mjs', import.meta.url))
  const launched = spawnSync(process.execPath, [runner, '--phase', phase], {
    encoding: 'utf8', timeout: 120000,
  })
  assert.equal(launched.status, 0, `${launched.error ?? ''}\n${launched.stdout}\n${launched.stderr}`)
  assert.equal(JSON.parse(launched.stdout.trim()).physicalCleanup, true)
})
}
