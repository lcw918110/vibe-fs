import assert from 'node:assert/strict'
import test from 'node:test'
import * as identity from '../../../dist/Participant/Persona/Surface.js'
import * as capability from '../../../dist/Participant/Persona/OfficeCapabilitySurface.js'
import * as handles from '../../../dist/Execution/Delegation/Handle/Surface.js'

test('WHAT[managed-session-lifecycle-023] production identity resolution rejects legacy names without promoting them', () => {
  for (const name of identity.legacyNames) {
    const result = identity.resolveParticipantIdentityAtRoot(name)
    assert.equal(result.ok, false, name)
    assert.equal(result.identity, null, name)
    assert.equal(result.error, 'LegacyParticipantName', name)
  }
  const engineer = identity.resolveParticipantIdentityAtRoot('engineer')
  assert.equal(engineer.ok, true)
  assert.equal(engineer.identity.role, 'engineer')
})

test('WHAT[managed-session-lifecycle-023] discovering a historical active role through recovery durably retires it and drains resources without upgrading authority (GAP-133, GAP-124)', () => {
  // WHAT[managed-session-lifecycle-023]:
  // 1. 历史旧角色活跃会话在恢复时不得自动升级为 Engineer，也不得获得任何写或 Fission 权限
  for (const legacy of ['coder', 'inspector', 'browser', 'inquiry', 'distiller']) {
    const resolved = identity.resolveParticipantIdentityAtRoot(legacy)
    assert.equal(resolved.ok, false)
    assert.equal(capability.isAllowed(legacy, 'Write'), false)
    assert.equal(capability.isAllowed(legacy, 'Fission'), false)
  }

  // 2. 模拟恢复发现遗留的旧角色活跃 handle
  const initial = handles.emptyState()
  const legacyHandle = handles.handleIdAgent('h_legacy_inspector')
  const activeLegacy = handles.apply(initial, {
    op: 'link',
    handle: legacyHandle,
    child: 'ses_legacy_inspector_child',
    agent: 'inspector',
    role: 'Inspector',
  })

  assert.equal(activeLegacy.ok, true)
  const activeRecord = handles.read(activeLegacy.state, legacyHandle)
  assert.equal(activeRecord.child, 'ses_legacy_inspector_child')

  // 3. 显式收束为终态（退役/放弃）排空资源
  const retired = handles.apply(activeLegacy.state, {
    op: 'abandon',
    handle: legacyHandle,
    reason: 'ParentCancelled',
  })
  assert.equal(retired.ok, true)
  assert.equal(handles.isAbandoned(retired.state, legacyHandle), true)

  // 4. 不可复活或重新链接到活跃状态
  const revive = handles.apply(retired.state, {
    op: 'link',
    handle: legacyHandle,
    child: 'ses_legacy_inspector_child_new',
    agent: 'inspector',
    role: 'Inspector',
  })
  assert.equal(revive.ok, false, 'retired/abandoned historical handle must never be revived')
})
