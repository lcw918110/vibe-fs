import test from 'node:test'

{
const { default: assert } = await import("node:assert/strict");
const { default: test } = await import("node:test");
const assoc = await import("../../../dist/Execution/Session/AssociationSurface.js");

const linked = (pairs, start = assoc.empty) =>
  pairs.reduce((state, pair) => {
    const result = assoc.link(pair, state)
    assert.equal(result.ok, true, result.message)
    return result.value
  }, start)

test('WHAT[session-ontology-006] HOST_008_work_parent_is_recorded_when_supplied', () => {
  const state = linked([{ main: 'ses_child', companion: 'ses_child_y', parent: 'ses_parent' }])
  assert.equal(assoc.entry('ses_child', state).parent, 'ses_parent')
  assert.equal(assoc.entry('ses_child_y', state).parent, 'ses_child')
})
test('WHAT[session-ontology-006] HOST_008_relink_without_parent_does_not_erase_known_parent', () => {
  const withParent = linked([{ main: 'ses_child', companion: 'ses_y', parent: 'ses_parent' }])
  const relinked = linked([{ main: 'ses_child', companion: 'ses_y' }], withParent)
  assert.equal(assoc.entry('ses_child', relinked).parent, 'ses_parent')
})
}

{
const { default: assert } = await import("node:assert/strict");
const { default: test } = await import("node:test");
const {
  flattenedChildAdapterProbe,
  restoredOwnerFlatteningProbe,
  unknownParentQueryErrorProbe,
  ancestryCycleProbe,
} = await import("../../../dist/OpenCode/Host/SessionsSurface.js");

test('WHAT[session-ontology-006] flattenedChildAdapterProbe confirms managed child physical parent is family root in-process', async () => {
  const observed = await flattenedChildAdapterProbe();
  assert.equal(observed.physicalParents.length, 2);
  assert.equal(observed.physicalParents[0], observed.root);
  assert.equal(observed.physicalParents[1], observed.root);
  assert.equal(observed.workerFamily, observed.root);
});

test('WHAT[session-ontology-006] restored child owner with empty process-local map flattens physical parent to authoritative Host root', async () => {
  const observed = await restoredOwnerFlatteningProbe();
  assert.equal(observed.firstOk, true);
  assert.equal(observed.secondOk, true);
  assert.deepEqual(observed.physicalParents, ['host-family-root', 'host-family-root']);
  assert.deepEqual(observed.listParents, ['host-family-root']);
  assert.deepEqual(observed.parentQueries, ['restored-child', 'intermediate-sub', 'host-family-root']);
  assert.equal(observed.queriesAfterFirst, 3);
  assert.equal(observed.queriesAfterSecond, 3, 'in-process proved ancestry must avoid extra Host queries');
  assert.equal(observed.childLanguage, observed.ownerLanguage, 'owner and child read the same live global language');
  assert.equal(observed.firstChildFamilyRoot, 'host-family-root');
  assert.equal(observed.ownerFamilyRoot, 'host-family-root');
});

test('WHAT[session-ontology-006] unknown parent query error fails closed and creates no session', async () => {
  const observed = await unknownParentQueryErrorProbe();
  assert.equal(observed.createOk, false);
  assert.equal(observed.createError, 'Host transport connection reset');
  assert.equal(observed.createCalls, 0, 'must not create session when parent resolution fails');
  assert.equal(observed.listOk, false);
  assert.equal(observed.listError, 'Host transport connection reset');
  assert.equal(observed.listCalls, 0);
});

test('WHAT[session-ontology-006] session parent cycle fails closed and refuses creation', async () => {
  const observed = await ancestryCycleProbe();
  assert.equal(observed.createOk, false);
  assert.match(observed.createError, /cycle detected/i);
  assert.equal(observed.createCalls, 0, 'must not create session when parent cycle detected');
});
}
