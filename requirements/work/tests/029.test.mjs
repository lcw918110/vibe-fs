import assert from 'node:assert/strict'
import fs from 'node:fs'
import os from 'node:os'
import path from 'node:path'
import test from 'node:test'
import * as surface from '../../../dist/Mission/Planning/Surface.js'
import * as registry from '../../../dist/OpenCode/Tools/ToolRegistrySurface.js'
import * as journalSurface from '../../../dist/Persistence/Journal/Surface.js'
import { acceptAuthorityRoot, openIncumbency, withExecutablePlugin } from '../../verification-system/tests/support/plugin-fixture.mjs'

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

test('WHAT[work-029] the draft is read and validated at the canonical resolved path', () => {
  const tmpRoot = fs.mkdtempSync(path.join(os.tmpdir(), 'plan-test-'))

  try {
    const workId = 'work-e2e-plan'

    // 1. Initially plan file does not exist: validation fails closed
    const missing = surface.PlanningSurface.readAndValidatePlan(tmpRoot, workId)
    assert.equal(missing.ok, false)
    assert.match(missing.error, /empty or missing/)

    // 2. Resolve canonical plan path via public helper and write draft via ordinary tools
    const resolved = surface.PlanningSurface.resolvePlanPath(tmpRoot, workId)
    assert.equal(resolved.ok, true)
    fs.mkdirSync(path.dirname(resolved.path), { recursive: true })
    const initialContent = '# Initial Plan\n- Step 1: Survey facts\n- Step 2: Formalize goal'
    fs.writeFileSync(resolved.path, initialContent)

    // 3. Validation via readAndValidatePlan verifies path and sha256 digest
    const validated = surface.PlanningSurface.readAndValidatePlan(tmpRoot, workId)
    assert.equal(validated.ok, true)
    assert.equal(validated.path, resolved.path)
    assert.ok(typeof validated.digest === 'string' && validated.digest.length === 64, 'digest must be 64-char sha256')

    // 4. Ordinary file update updates validated digest
    const updatedContent = initialContent + '\n- Step 3: Implement'
    fs.writeFileSync(resolved.path, updatedContent)
    const revalidated = surface.PlanningSurface.readAndValidatePlan(tmpRoot, workId)
    assert.equal(revalidated.ok, true)
    assert.equal(revalidated.path, resolved.path)
    assert.notEqual(revalidated.digest, validated.digest)

    // 5. Empty or whitespace draft is rejected
    fs.writeFileSync(resolved.path, '   \n')
    const emptyRes = surface.PlanningSurface.readAndValidatePlan(tmpRoot, workId)
    assert.equal(emptyRes.ok, false)
    assert.match(emptyRes.error, /plan artifact is empty/)
  } finally {
    fs.rmSync(tmpRoot, { recursive: true, force: true })
  }
})

test('WHAT[work-029] shared Plan interface reads the ordinary draft path and rejects retired members', async () => {
  // The shared Plan interface is exercised through the REAL Plan programming
  // surface (`todowrite-for-plan`), which is the only Plan entry the host
  // registers. The retired legacy Plan entry is gone, so this is also the
  // proof that the live surface carries the same interface.
  await withExecutablePlugin(async (hooks, directory, _createdIds, runtime) => {
    const sessionID = 'ses-work-029-plan-surface'
    await acceptAuthorityRoot(runtime, sessionID, 'plan', 'user-plan-root')
    await openIncumbency(runtime, sessionID, 'inc-work-029', 'S1')

    const commonDir = await journalSurface.JournalSurface_initializeStateRoot(directory)
    const planRoot = path.join(commonDir, 'wanxiangshu')
    const opened = await surface.PlanningSurface.ensurePlanWorkOpened(commonDir, directory, sessionID, 0n)
    assert.equal(opened.ok, true, opened.error)

    let programSequence = 0
    const run = body => hooks.tool['todowrite-for-plan'].execute(
      { program: `class Js extends JsProgram { async run() { ${body} } }`, name: 'plan-read-' + (++programSequence),
          intent: 'Wait for this plan file operation while preserving the same request deadline.',
        },
      { sessionID, agent: 'plan', callID: 'call-work-029-' + programSequence, messageID: 'run-work-029', providerRunID: 'run-work-029' },
    )

    // The draft lives at the live surface's derived plan path (.Herdchestra/wanxiangshu/plan/<sessionID>/plan.md).
    const draftRes = surface.PlanningSurface.resolvePlanPath(planRoot, sessionID)
    assert.equal(draftRes.ok, true, draftRes.error)
    const draft = draftRes.path
    fs.mkdirSync(path.dirname(draft), { recursive: true })
    fs.writeFileSync(draft, '# Draft')

    const relDraft = path.relative(directory, draft)
    const read = String(await run(`return { text: await this.cat(${JSON.stringify(relDraft)}), retired: [typeof this.file, typeof this.edit, typeof this.rewrite, typeof this.reviewOnly] };`))
    assert.match(read, /# Draft/)
    assert.match(read, /undefined/)

    const retired = String(await run('return { retired: typeof this.file };'))
    assert.match(retired, /undefined/)
  })
})

test('WHAT[work-029] localized Plan pseudocode describes the same shared interface', () => {
  for (const language of ['en', 'zh-CN']) {
    const description = registry.jsPlanDescription(language)
    assert.equal(description.includes('{{'), false)
    for (const signature of ['async cat(path)', 'write(path, content)', 'async ask(question)']) {
      assert.ok(description.includes(signature), signature)
    }
    for (const retired of ['this.file(', 'this.edit(', 'this.rewrite(']) assert.equal(description.includes(retired), false)
    assert.equal(description.includes('_api'), false)
    assert.equal(description.includes('this.ls('), false)
    assert.equal(description.includes('this.rg('), false)
  }
})
