import assert from 'node:assert/strict'
import test from 'node:test'
import * as owner from '../../../dist/Participant/Provider/Attempt/Fallback/ProviderFailureSurface.js'

test('stop-wait timeout is not supersession and not delegate success', () => {
  assert.equal(owner.stopWaitVerdict(false, 'TimedOut'), 'ReceiptUnknown')
  assert.notEqual(owner.stopWaitVerdict(false, 'TimedOut'), 'Superseded')
  assert.equal(owner.delegateRetryObservation('ReceiptUnknown').startsWith('Error:'), true)
  assert.notEqual(owner.delegateRetryObservation('ReceiptUnknown'), 'Ok')
  assert.equal(owner.delegateRetryObservation('ReceiptUnknown').includes('Host did not stop'), false)
  assert.equal(owner.stopWaitVerdict(true, 'Stopped'), 'Superseded')
  const admittedTimeout = owner.stopWaitVerdict(true, 'TimedOut')
  assert.notEqual(admittedTimeout, 'Superseded')
  assert.notEqual(owner.delegateRetryObservation(admittedTimeout), 'Ok')
})

test('a refused stop wait is not a missing receipt, not supersession, and not success', () => {
  assert.equal(owner.stopWaitVerdict(false, 'Denied'), 'WaitDenied')
  assert.equal(owner.stopWaitVerdict(true, 'Denied'), 'WaitDenied')
  assert.notEqual(owner.stopWaitVerdict(false, 'Denied'), 'ReceiptUnknown')
  assert.notEqual(owner.stopWaitVerdict(false, 'Denied'), 'Superseded')
  assert.notEqual(owner.delegateRetryObservation('WaitDenied'), 'Ok')
  assert.equal(owner.delegateRetryObservation('WaitDenied').includes('Host did not stop'), false)
})
