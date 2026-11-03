import assert from 'node:assert/strict'
import { mkdtempSync, rmSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import test from 'node:test'
import * as temporal from '../../../dist/Verification/TemporalSurface.js'
import * as fork from '../../../dist/Execution/Delegation/Fork/OpenCode/ToolSurface.js'

const deferred = () => {
  let resolve
  let reject
  const promise = new Promise((accept, refuse) => { resolve = accept; reject = refuse })
  return { promise, resolve, reject }
}
const checkpoint = () => new Promise(resolve => setImmediate(resolve))

test('the Plugin owner starts accepted work synchronously and releases resources after both work paths drain', async () => {
  const foreground = deferred()
  const background = deferred()
  let releases = 0
  let foregroundStarted = false
  let backgroundStarted = false
  let lateStarted = false
  const scope = temporal.createPluginWorkScope(async () => { releases += 1 })
  const front = temporal.runPluginWork(scope, () => {
    foregroundStarted = true
    return foreground.promise
  })
  const back = temporal.backgroundPluginWork(scope, () => {
    backgroundStarted = true
    return background.promise
  })
  try {
    assert.equal(foregroundStarted, true)
    assert.equal(backgroundStarted, true)
    const closing = temporal.disposePluginWorkScope(scope)
    await temporal.runPluginWork(scope, async () => { lateStarted = true })
    await temporal.backgroundPluginWork(scope, async () => { lateStarted = true })
    await checkpoint()
    assert.equal(releases, 0)
    assert.equal(lateStarted, false)
    background.resolve()
    await back
    await checkpoint()
    assert.equal(releases, 0, 'the admitted foreground callback still owns its work')
    foreground.resolve()
    await front
    await closing
    assert.equal(releases, 1)
    await temporal.disposePluginWorkScope(scope)
    assert.equal(releases, 1)
  } finally {
    foreground.resolve()
    background.resolve()
    await Promise.allSettled([front, back])
    await temporal.disposePluginWorkScope(scope).catch(() => {})
  }
})

test('Plugin foreground synchronous throws and asynchronous rejections retain identity without poisoning close', async () => {
  let releases = 0
  const scope = temporal.createPluginWorkScope(async () => { releases += 1 })
  const synchronous = new Error('foreground synchronous failure')
  const asynchronous = new Error('foreground asynchronous failure')
  try {
    await assert.rejects(temporal.runPluginWork(scope, () => { throw synchronous }), error => error === synchronous)
    await assert.rejects(temporal.runPluginWork(scope, () => Promise.reject(asynchronous)), error => error === asynchronous)
    await temporal.backgroundPluginWork(scope, async () => {})
    await temporal.disposePluginWorkScope(scope)
    assert.equal(releases, 1)
  } finally {
    await temporal.disposePluginWorkScope(scope).catch(() => {})
  }
})

test('Plugin close reports the last observed background Error and still releases its resource owner', async () => {
  const firstStarted = deferred()
  const secondStarted = deferred()
  const firstObserved = new Error('second callback fails first')
  const lastObserved = new Error('first callback fails last')
  let releases = 0
  const scope = temporal.createPluginWorkScope(async () => { releases += 1 })
  const first = temporal.backgroundPluginWork(scope, () => firstStarted.promise)
  const second = temporal.backgroundPluginWork(scope, () => secondStarted.promise)
  const closing = temporal.disposePluginWorkScope(scope)
  const closed = assert.rejects(closing, error => error === lastObserved)
  try {
    secondStarted.reject(firstObserved)
    await second
    assert.equal(releases, 0)
    firstStarted.reject(lastObserved)
    await first
    await closed
    assert.equal(releases, 1, 'an observed background failure cannot skip resource cleanup')
    let lateStarted = false
    await temporal.backgroundPluginWork(scope, async () => { lateStarted = true })
    assert.equal(lateStarted, false)
  } finally {
    firstStarted.resolve()
    secondStarted.resolve()
    await Promise.allSettled([first, second, closed])
    await temporal.disposePluginWorkScope(scope).catch(() => {})
  }
})

const withForkOwner = async (label, run) => {
  const directory = mkdtempSync(join(tmpdir(), `wxs-task-scope-${label}-`))
  const owner = `manager-task-scope-${label}`
  const runtime = await fork.createRuntime(directory, [{ sessionId: owner, agent: 'manager' }])
  try {
    return await run(runtime, owner, fork.captureOwnerWork(runtime, owner))
  } finally {
    await fork.detachToolRuntime(runtime).catch(() => {})
    fork.disposeRuntime(runtime)
    rmSync(directory, { recursive: true, force: true })
  }
}

test('the Fork owner waits for the current busy period, admits the next one, and closes admission on detach', async () => {
  await withForkOwner('idle-detach', async (_runtime, _owner, work) => {
    const first = deferred()
    const second = deferred()
    let starts = 0
    fork.trackOwnerWork(work, () => { starts += 1; return first.promise })
    assert.equal(starts, 1)
    const idle = fork.awaitOwnerObservedWork(work)
    const otherIdle = fork.awaitOwnerObservedWork(work)
    let idleReturned = false
    idle.then(() => { idleReturned = true })
    try {
      await checkpoint()
      assert.equal(idleReturned, false)
      first.resolve()
      await Promise.all([idle, otherIdle])
      fork.trackOwnerWork(work, () => { starts += 1; return second.promise })
      assert.equal(starts, 2, 'observing idle does not close callback admission')
      let detached = false
      const closing = fork.detachOwnerWork(work).then(() => { detached = true })
      await checkpoint()
      fork.trackOwnerWork(work, async () => { starts += 1 })
      assert.equal(starts, 2, 'the original owner refuses callbacks after detach reaches its work drain')
      assert.equal(detached, false)
      second.resolve()
      await closing
      fork.trackOwnerWork(work, async () => { starts += 1 })
      assert.equal(starts, 2)
      await fork.detachOwnerWork(work)
    } finally {
      first.resolve()
      second.resolve()
      await Promise.allSettled([idle, otherIdle])
      await fork.detachOwnerWork(work).catch(() => {})
    }
  })
})

test('Fork idle and detach preserve the last observed background Error across a successful later busy period', async () => {
  await withForkOwner('failure', async (_runtime, _owner, work) => {
    const firstStarted = deferred()
    const secondStarted = deferred()
    const firstObserved = new Error('Fork second callback fails first')
    const lastObserved = new Error('Fork first callback fails last')
    fork.trackOwnerWork(work, () => firstStarted.promise)
    fork.trackOwnerWork(work, () => secondStarted.promise)
    const observed = assert.rejects(fork.awaitOwnerObservedWork(work), error => error === lastObserved)
    try {
      secondStarted.reject(firstObserved)
      await checkpoint()
      firstStarted.reject(lastObserved)
      await observed
      let nextStarted = false
      fork.trackOwnerWork(work, async () => { nextStarted = true })
      assert.equal(nextStarted, true)
      await assert.rejects(fork.awaitOwnerObservedWork(work), error => error === lastObserved)
      await assert.rejects(fork.detachOwnerWork(work), error => error === lastObserved)
    } finally {
      firstStarted.resolve()
      secondStarted.resolve()
      await observed.catch(() => {})
      await fork.detachOwnerWork(work).catch(() => {})
    }
  })
})

test('parent cancellation with the actual fixed DevOps binding waits for callbacks without closing their owner', async () => {
  await withForkOwner('fixed-devops', async (runtime, owner, work) => {
    const current = deferred()
    let starts = 0
    let cancelled = false
    assert.match(await fork.executeHorizon(runtime, owner), /devops/)
    assert.equal(fork.durableLifecycleByname(runtime, owner, 'devops'), 'Active')
    fork.trackOwnerWork(work, () => { starts += 1; return current.promise })
    const cancelling = fork.cancelOwnerChildren(runtime, owner).then(() => { cancelled = true })
    try {
      await checkpoint()
      assert.equal(cancelled, false)
      current.resolve()
      await cancelling
      fork.trackOwnerWork(work, async () => { starts += 1 })
      assert.equal(starts, 2, 'the retained DevOps branch observes idle and keeps callback admission open')
      await fork.awaitOwnerObservedWork(work)
      assert.equal(fork.durableLifecycleByname(runtime, owner, 'devops'), 'Active')
      assert.equal(fork.abortCount(runtime), 0)
      await fork.detachOwnerWork(work)
      fork.trackOwnerWork(work, async () => { starts += 1 })
      assert.equal(starts, 2)
    } finally {
      current.resolve()
      await cancelling
      await fork.detachOwnerWork(work).catch(() => {})
    }
  })
})
