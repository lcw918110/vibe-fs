import assert from 'node:assert/strict'
import test from 'node:test'
import { assertJsData } from '../../verification-system/tests/support/js-contract.mjs'

const identity = await import('../../../dist/Participant/Persona/Surface.js')

const EXPECTED = {
  orchestrator: { role: 'orchestrator', persona: 'Director' },
  manager: { role: 'manager', persona: 'Lead' },
  engineer: { role: 'engineer', persona: 'Engineer' },
  devops: { role: 'devops', persona: 'Operator' },
  bookkeeper: { role: 'bookkeeper', persona: 'Curator' },
}

const expectedView = (name, origin = 'ResolvedAtRoot') => ({
  name,
  role: EXPECTED[name].role,
  persona: EXPECTED[name].persona,
  catalogVersion: 1,
  origin,
})

const assertCanonicalIdentity = (actual, expected, label) => {
  assert.equal(actual.name, expected.name, `${label} participant`)
  assert.equal(actual.role, expected.role, `${label} role`)
  assert.equal(actual.persona, expected.persona, `${label} persona`)
  assert.equal(actual.catalogVersion, expected.catalogVersion, `${label} version`)
  assert.equal(actual.origin, expected.origin, `${label} origin`)
}

const rehydrate = (view, ownerName = '') =>
  identity.rehydrateParticipantIdentity(
    ownerName,
    view.name,
    view.role,
    'deep',
    'retired-peer-slot',
    view.persona,
    view.catalogVersion,
    view.origin,
  )

const assertError = (result, error) => {
  assertJsData(result, error)
  assert.equal(result.ok, false)
  assert.equal(result.identity, null)
  assert.equal(result.error, error)
}

test('WHAT[participant-identity-003] rejects blank Persona and unsupported catalog version', () => {
  assertError(rehydrate({ ...expectedView('engineer'), persona: '  ' }), 'BlankPersona')
  assertError(
    rehydrate({ ...expectedView('engineer'), catalogVersion: 2 }),
    'UnsupportedPersonaCatalogVersion',
  )
})
