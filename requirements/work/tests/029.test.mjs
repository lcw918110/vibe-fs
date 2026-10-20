import assert from 'node:assert/strict'
import { execFileSync } from 'node:child_process'
import fs from 'node:fs'
import os from 'node:os'
import path from 'node:path'
import test from 'node:test'
import * as surface from '../../../dist/Mission/Planning/Surface.js'
import * as registry from '../../../dist/OpenCode/Tools/ToolRegistrySurface.js'
import * as lifecycle from '../../../dist/Mission/Lifecycle/Surface.js'
import * as workspaceEvents from '../../../dist/OpenCode/Host/WorkspaceEventStoreSurface.js'
import * as journalSurface from '../../../dist/Persistence/Journal/Surface.js'

// The sandbox resolves the active tenure from the unified lifecycle work, so the
// store is a real workspace store with one opened plan work.
const openPlanStore = async (root, workId) => {
  execFileSync('git', ['init', '--quiet', root])
  const store = workspaceEvents.acquire(await journalSurface.JournalSurface_initializeStateRoot(root))
  const opened = await lifecycle.seedOpen(store, 'plan', workId, root, 1n)
  assert.equal(opened.ok, true, opened.error)
  return store
}

test('WHAT[work-029] Plan path derivation resolves deterministically and rejects path traversal', () => {
  const root = '/workspace/test-repo/.Herdchestra/wanxiangshu'

  // Valid work IDs
  const valid1 = surface.PlanningSurface.resolvePlanPath(root, 'work-alpha')
  assert.equal(valid1.ok, true)
  assert.equal(valid1.path, '/workspace/test-repo/.Herdchestra/wanxiangshu/plan/work-alpha/plan.md')

  const valid2 = surface.PlanningSurface.resolvePlanPath(root, 'task_42-v1')
  assert.equal(valid2.ok, true)
  assert.equal(valid2.path, '/workspace/test-repo/.Herdchestra/wanxiangshu/plan/task_42-v1/plan.md')

  // Insecure work IDs / path traversal attempts MUST be rejected
  const traversalAttempts = [
    '../escape',
    '../../etc/passwd',
    'work/nested/../../escape',
    '/absolute/work',
    '..',
    '.hidden',
    'work\\traversal',
    'work\0nullbyte',
    '',
    '   ',
  ]

  for (const badId of traversalAttempts) {
    const res = surface.PlanningSurface.resolvePlanPath(root, badId)
    assert.equal(res.ok, false, `Traversal attempt "${badId}" must be rejected`)
    assert.ok(typeof res.error === 'string' && res.error.length > 0)
  }
})

test('WHAT[work-029] P is read, rewritten atomically with exclusive temp file, and edited with patch replacement', () => {
  const tmpRoot = fs.mkdtempSync(path.join(os.tmpdir(), 'plan-test-'))

  try {
    const workId = 'work-e2e-plan'

    // 1. Initially plan file does not exist
    const readMissing = surface.PlanningSurface.readPlan(tmpRoot, workId)
    assert.equal(readMissing.ok, false)

    // 2. Rewrite plan atomically
    const initialContent = '# Initial Plan\n- Step 1: Survey facts\n- Step 2: Formalize goal'
    const writeRes = surface.PlanningSurface.rewritePlanAtomic(tmpRoot, workId, initialContent)
    assert.equal(writeRes.ok, true)
    assert.ok(typeof writeRes.digest === 'string' && writeRes.digest.length === 64, 'digest must be 64-char sha256')
    assert.ok(fs.existsSync(writeRes.path), 'plan.md must exist on disk')

    // 3. Read back verified
    const readBack = surface.PlanningSurface.readPlan(tmpRoot, workId)
    assert.equal(readBack.ok, true)
    assert.equal(readBack.content, initialContent)

    // 4. Edit plan with patches
    const patches = [
      { find: '- Step 2: Formalize goal', put: '- Step 2: Formalize goal\n- Step 3: Implement' },
    ]
    const editRes = surface.PlanningSurface.editPlanAtomic(tmpRoot, workId, patches)
    assert.equal(editRes.ok, true)

    const readEdited = surface.PlanningSurface.readPlan(tmpRoot, workId)
    assert.equal(readEdited.ok, true)
    assert.ok(readEdited.content.includes('- Step 3: Implement'))

    // 5. Missing patch find target returns error
    const badPatch = [{ find: 'non-existent target string', put: 'replacement' }]
    const badEdit = surface.PlanningSurface.editPlanAtomic(tmpRoot, workId, badPatch)
    assert.equal(badEdit.ok, false)
    assert.match(badEdit.error, /Patch find target not found/)
  } finally {
    fs.rmSync(tmpRoot, { recursive: true, force: true })
  }
})

test('WHAT[work-029] todowrite-for-plan programming surface runs via class Js extends JsProgram and strictly forbids path arguments', async () => {
  const tmpRoot = fs.mkdtempSync(path.join(os.tmpdir(), 'plan-prog-test-'))
  const workId = 'work-jsplan-paths'

  try {
    const initialContent = '# Initial Plan\n- Step 1: Draft\n- Step 2: Review'
    surface.PlanningSurface.rewritePlanAtomic(tmpRoot, workId, initialContent)
    const store = await openPlanStore(tmpRoot, workId)

    // 1. Passing a path to this.file('plan.md') MUST fail with PATH_DENIED
    const fileWithPath = `class Js extends JsProgram {
      async run() {
        const f = await this.file('plan.md');
        return { text: f.text() };
      }
    }`
    const resFileWithPath = await registry.runJsPlan(store.gitCommonDir, tmpRoot, workId, fileWithPath)
    assert.match(resFileWithPath, /# failed/)
    assert.match(resFileWithPath, /code = "PATH_DENIED"/)

    // 2. Passing a path to this.edit('plan.md', ...) MUST fail with PATH_DENIED
    const editWithPath = `class Js extends JsProgram {
      async run() {
        this.edit('plan.md', { find: 'Draft', put: 'Rewrite' });
        return { ok: true };
      }
    }`
    const resEditWithPath = await registry.runJsPlan(store.gitCommonDir, tmpRoot, workId, editWithPath)
    assert.match(resEditWithPath, /# failed/)
    assert.match(resEditWithPath, /code = "PATH_DENIED"/)

    // 3. Passing a path to this.rewrite('plan.md', ...) MUST fail with PATH_DENIED
    const rewriteWithPath = `class Js extends JsProgram {
      async run() {
        this.rewrite('plan.md', 'new content');
        return { ok: true };
      }
    }`
    const resRewriteWithPath = await registry.runJsPlan(store.gitCommonDir, tmpRoot, workId, rewriteWithPath)
    assert.match(resRewriteWithPath, /# failed/)
    assert.match(resRewriteWithPath, /code = "PATH_DENIED"/)
  } finally {
    fs.rmSync(tmpRoot, { recursive: true, force: true })
  }
})

test('WHAT[work-029] unreviewed tenure permits todowrite-for-plan file reading but blocks edits and rewrites until review; review unlocks writes and relay re-locks', async () => {
  const tmpRoot = fs.mkdtempSync(path.join(os.tmpdir(), 'plan-review-gate-test-'))
  const workId = 'work-jsplan-review-gate'

  try {
    const initialContent = '# Initial Plan\n- Step 1: Draft\n- Step 2: Review'
    surface.PlanningSurface.rewritePlanAtomic(tmpRoot, workId, initialContent)
    const store = await openPlanStore(tmpRoot, workId)

    // 1. In unreviewed tenure: read without path succeeds
    const readProgram = `class Js extends JsProgram {
      async run() {
        const f = await this.file([['step1', 'step1end', 'Step 1']]);
        return { slice: f.text('step1', '$') };
      }
    }`
    const readRes = await registry.runJsPlan(store.gitCommonDir, tmpRoot, workId, readProgram)
    assert.match(readRes, /# ok/)
    assert.match(readRes, /Step 1: Draft/)

    // 2. In unreviewed tenure: edit without path fails with PLAN_REVIEW_REQUIRED
    const editProgram = `class Js extends JsProgram {
      async run() {
        this.edit({ find: 'Draft', put: 'Final' });
        return { edited: true };
      }
    }`
    const editRes = await registry.runJsPlan(store.gitCommonDir, tmpRoot, workId, editProgram)
    assert.match(editRes, /# failed/)
    assert.match(editRes, /code = "PLAN_REVIEW_REQUIRED"/)
    assert.match(editRes, /Review is required before modifying the plan artifact/)

    // Verify file content is unchanged on disk
    assert.equal(surface.PlanningSurface.readPlan(tmpRoot, workId).content, initialContent)

    // 3. In unreviewed tenure: rewrite without path fails with PLAN_REVIEW_REQUIRED
    const rewriteProgram = `class Js extends JsProgram {
      async run() {
        this.rewrite('# Completely New Plan');
        return { done: true };
      }
    }`
    const rewriteRes = await registry.runJsPlan(store.gitCommonDir, tmpRoot, workId, rewriteProgram)
    assert.match(rewriteRes, /# failed/)
    assert.match(rewriteRes, /code = "PLAN_REVIEW_REQUIRED"/)
    assert.match(rewriteRes, /Review is required before modifying the plan artifact/)
    assert.equal(surface.PlanningSurface.readPlan(tmpRoot, workId).content, initialContent)

    // 4. Submit review for current tenure: the accepted review is a unified
    //    lifecycle Reviewed event (Stayed).
    const reviewed = await lifecycle.seedReview(store, workId, 'Stayed')
    assert.equal(reviewed.ok, true)

    // 5. Now tenure inc-1 has been reviewed -> rewrite succeeds!
    const rewriteAfterReviewRes = await registry.runJsPlan(store.gitCommonDir, tmpRoot, workId, rewriteProgram)
    assert.match(rewriteAfterReviewRes, /# ok/)
    assert.match(rewriteAfterReviewRes, /plan\.md/)
    assert.equal(surface.PlanningSurface.readPlan(tmpRoot, workId).content, '# Completely New Plan')

    // 6. Now edit succeeds as well
    const editAfterReviewProgram = `class Js extends JsProgram {
      async run() {
        this.edit({ find: 'Completely New', put: 'Reviewed' });
        return { edited: true };
      }
    }`
    const editAfterReviewRes = await registry.runJsPlan(store.gitCommonDir, tmpRoot, workId, editAfterReviewProgram)
    assert.match(editAfterReviewRes, /# ok/)
    assert.equal(surface.PlanningSurface.readPlan(tmpRoot, workId).content, '# Reviewed Plan')

    // 7. Relay to next tenure: the reviewed (Stayed) tenure relays — one append
    //    retires inc-1 Relayed and opens the successor inc-2 at S1.
    const relayed = await lifecycle.seedDeliver(store, workId, 'run-029-relay', 'call-029-relay', 11n)
    assert.equal(relayed.ok, true, relayed.error)

    // 8. Next tenure inc-2 has NOT yet submitted review -> mutation MUST be blocked again!
    const writeInInc2 = `class Js extends JsProgram {
      async run() {
        this.rewrite('# Attempt In Second Tenure Before Review');
        return { done: true };
      }
    }`
    const resWriteInInc2 = await registry.runJsPlan(store.gitCommonDir, tmpRoot, workId, writeInInc2)
    assert.match(resWriteInInc2, /# failed/)
    assert.match(resWriteInInc2, /code = "PLAN_REVIEW_REQUIRED"/)
    assert.match(resWriteInInc2, /Review is required before modifying the plan artifact/)
    // File remains unchanged
    assert.equal(surface.PlanningSurface.readPlan(tmpRoot, workId).content, '# Reviewed Plan')

    // 9. Reading in inc-2 succeeds
    const readInInc2Program = `class Js extends JsProgram {
      async run() {
        const f = await this.file([['h1', 'h1end', '# Reviewed Plan']]);
        return { text: f.text('^', '$') };
      }
    }`
    const readInInc2Res = await registry.runJsPlan(store.gitCommonDir, tmpRoot, workId, readInInc2Program)
    assert.match(readInInc2Res, /# ok/)
    assert.match(readInInc2Res, /# Reviewed Plan/)
  } finally {
    fs.rmSync(tmpRoot, { recursive: true, force: true })
  }
})

test('WHAT[work-029] todowrite-for-plan tool description reuses the shared anchor tutorial with pathless signatures', () => {
  for (const language of ['en', 'zh-CN']) {
    const description = registry.jsPlanDescription(language)
    const text = description.replace(/\s+/g, ' ')
    assert.equal(text.includes('{{'), false, `${language} description retains a placeholder`)
    assert.equal((description.match(/class Js extends JsProgram/g) ?? []).length, 1)
    assert.equal(description.includes('_api'), false)
    assert.equal(description.includes('__jsFailure'), false)
    assert.doesNotMatch(description, /file\(path/)
    assert.doesNotMatch(description, /edit\(path/)
    assert.doesNotMatch(description, /rewrite\(path/)
    assert.doesNotMatch(description, /this\.edit\("[^"]+",/)
    assert.doesNotMatch(description, /glob\(/)
    assert.doesNotMatch(description, /grep\(/)
  }

  const english = registry.jsPlanDescription('en').replace(/\s+/g, ' ')
  for (const lesson of [
    'PATH_DENIED',
    'review',
    'file(matches = [])',
    'edit(changes)',
    'rewrite(newText)',
    'ordered anchors',
    'text(from, to)',
    'name+N / name-N',
    'not a line number',
    'file(matches) + text() + rewrite()',
    'Near matches are diagnostics, never write authority',
  ]) {
    assert.equal(english.includes(lesson), true, `English plan description missing: ${lesson}`)
  }

  const chinese = registry.jsPlanDescription('zh-CN').replace(/\s+/g, ' ')
  for (const lesson of [
    'PATH_DENIED',
    'review',
    'file(matches = [])',
    'edit(changes)',
    'rewrite(newText)',
    '有序 anchor',
    'text(from, to)',
    'name+N / name-N',
    '不是行号',
    'file(matches) + text() + rewrite()',
    '近似匹配只产生诊断，绝不获得写权限',
  ]) {
    assert.equal(chinese.includes(lesson), true, `Chinese plan description missing: ${lesson}`)
  }
})
