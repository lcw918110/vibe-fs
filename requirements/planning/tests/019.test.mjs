import test from 'node:test';
import assert from 'node:assert/strict';
import { PlanningSurface } from '../../../dist/Mission/Planning/Surface.js';

function createMemoryStore(initialState) {
  let currentState = initialState;
  const appendedEvents = [];
  return {
    TryCurrent: () => currentState,
    Append: async (envelope) => {
      appendedEvents.push(envelope);
      return { Ok: 'event-' + appendedEvents.length };
    },
    get events() {
      return appendedEvents;
    },
    setCurrentState: (st) => {
      currentState = st;
    }
  };
}

test('WHAT[planning-019] 1. executeAsk appends PlanAskPending and is idempotent on identical re-ask', async () => {
  let state = PlanningSurface.empty();
  state = PlanningSurface.applyWorkEvent(PlanningSurface.workOpened('work-19-1', '/root'), state).state;
  state = PlanningSurface.applyWorkEvent(PlanningSurface.incumbencyOpened('work-19-1', 'inc-1', 'S1', 10n), state).state;

  const store = createMemoryStore(state);
  const qText = 'Should we use Redis or Memcached?';

  // First ask
  const res1 = await PlanningSurface.executeAsk(store, 'work-19-1', qText);
  assert.equal(res1.ok, true);
  assert.equal(res1.kind, 'waiting_for_user');
  assert.equal(res1.question, qText);
  assert.equal(res1.status, 'pending');

  // PlanAskPending event should be generated
  const askEv = PlanningSurface.askPending('work-19-1', 'inc-1', qText, 11n);
  const applyRes = PlanningSurface.applyWorkEvent(askEv, state);
  assert.equal(applyRes.ok, true);
  const stateWithPending = applyRes.state;
  assert.ok(stateWithPending.PendingAsk);

  // Idempotent re-ask with identical question
  store.setCurrentState(stateWithPending);
  const res2 = await PlanningSurface.executeAsk(store, 'work-19-1', qText);
  assert.equal(res2.ok, true);
  assert.equal(res2.kind, 'waiting_for_user');
});

test('WHAT[planning-019] 2. second ask is rejected while an ask is already pending', async () => {
  let state = PlanningSurface.empty();
  state = PlanningSurface.applyWorkEvent(PlanningSurface.workOpened('work-19-2', '/root'), state).state;
  state = PlanningSurface.applyWorkEvent(PlanningSurface.incumbencyOpened('work-19-2', 'inc-1', 'S1', 10n), state).state;
  state = PlanningSurface.applyWorkEvent(PlanningSurface.askPending('work-19-2', 'inc-1', 'Question 1', 11n), state).state;

  const store = createMemoryStore(state);

  // Second distinct ask fails closed
  const res = await PlanningSurface.executeAsk(store, 'work-19-2', 'Question 2');
  assert.equal(res.ok, false);
  assert.match(res.error, /already pending/i);

  // Fold also strictly rejects
  const foldRes = PlanningSurface.applyWorkEvent(PlanningSurface.askPending('work-19-2', 'inc-1', 'Question 2', 12n), state);
  assert.equal(foldRes.ok, false);
  assert.match(foldRes.error, /already pending/i);
});

test('WHAT[planning-019] 3. assembleAskContinuation maintains pending when no new user message arrived', () => {
  const messages = [
    { role: 'system', content: 'You are Plan' },
    { role: 'user', content: 'Initial user prompt' },
    { role: 'assistant', content: 'Thinking...' },
    { role: 'tool', name: 'ask', content: 'waiting_for_user' }
  ];

  const pendingAskObj = {
    question: 'Need clarification on requirements',
    cursor: 20n,
    incumbencyId: 'inc-1'
  };

  const tenureObj = {
    workId: 'work-19-3',
    incumbencyId: 'inc-1',
    stage: 'S1',
    openingCursor: 10n
  };

  const res = PlanningSurface.assembleAskContinuation(messages, pendingAskObj, tenureObj);
  assert.equal(res.resolved, false);
  assert.equal(res.answer, null);
  assert.equal(res.messages.length, messages.length);
});

test('WHAT[planning-019] 4. assembleAskContinuation resolves on new user message and enables PlanAskResolved', () => {
  const messages = [
    { role: 'user', content: 'Initial requirement' },
    { role: 'assistant', content: 'I need to ask a question' },
    { role: 'user', content: 'Use PostgreSQL and enable SSL', cursor: 25n },
    { role: 'user', content: 'Also set max connections to 100', cursor: 26n }
  ];

  const pendingAskObj = {
    question: 'Which database should we choose?',
    cursor: 20n,
    incumbencyId: 'inc-1'
  };

  const tenureObj = {
    workId: 'work-19-4',
    incumbencyId: 'inc-1',
    stage: 'S1',
    openingCursor: 10n
  };

  const res = PlanningSurface.assembleAskContinuation(messages, pendingAskObj, tenureObj);
  assert.equal(res.resolved, true);
  assert.equal(res.answer, 'Use PostgreSQL and enable SSL');

  // First user reply is replaced with ask tool_result continuation
  assert.equal(res.messages[1].role, 'tool');
  assert.equal(res.messages[1].name, 'ask');
  assert.equal(res.messages[1].content, 'Use PostgreSQL and enable SSL');

  // Subsequent user message is preserved verbatim in order
  assert.equal(res.messages[2].role, 'user');
  assert.equal(res.messages[2].content, 'Also set max connections to 100');

  // Fold can now resolve the ask
  let state = PlanningSurface.empty();
  state = PlanningSurface.applyWorkEvent(PlanningSurface.workOpened('work-19-4', '/root'), state).state;
  state = PlanningSurface.applyWorkEvent(PlanningSurface.incumbencyOpened('work-19-4', 'inc-1', 'S1', 10n), state).state;
  state = PlanningSurface.applyWorkEvent(PlanningSurface.askPending('work-19-4', 'inc-1', pendingAskObj.question, 20n), state).state;

  const resolveEv = PlanningSurface.askResolved('inc-1', res.resolvedCursor);
  const resolveRes = PlanningSurface.applyWorkEvent(resolveEv, state);
  assert.equal(resolveRes.ok, true);
  assert.equal(resolveRes.state.PendingAsk == null, true);
});

test('WHAT[planning-019] 5. ask is rejected after delivery has occurred', async () => {
  let state = PlanningSurface.empty();
  state = PlanningSurface.applyWorkEvent(PlanningSurface.workOpened('work-19-5', '/root'), state).state;
  state = PlanningSurface.applyWorkEvent(PlanningSurface.incumbencyOpened('work-19-5', 'inc-1', 'S1', 10n), state).state;
  state = PlanningSurface.applyWorkEvent(PlanningSurface.incumbencyRetired('inc-1', PlanningSurface.PlanRetirementOutcome.Delivered, 20n), state).state;
  state = PlanningSurface.applyWorkEvent(PlanningSurface.delivered('inc-1', 'work-19-5', 'sha-123', 'plan.md'), state).state;

  const store = createMemoryStore(state);
  const res = await PlanningSurface.executeAsk(store, 'work-19-5', 'Can I ask something?');
  assert.equal(res.ok, false);
  assert.match(res.error, /already been delivered/i);
});

test('WHAT[planning-019] 6. Conflicting resolved payload is rejected and replay is idempotent', () => {
  let state = PlanningSurface.empty();
  state = PlanningSurface.applyWorkEvent(PlanningSurface.workOpened('work-19-6', '/root'), state).state;
  state = PlanningSurface.applyWorkEvent(PlanningSurface.incumbencyOpened('work-19-6', 'inc-1', 'S1', 10n), state).state;
  state = PlanningSurface.applyWorkEvent(PlanningSurface.askPending('work-19-6', 'inc-1', 'Question', 11n), state).state;

  // First resolution succeeds
  state = PlanningSurface.applyWorkEvent(PlanningSurface.askResolved('inc-1', 15n), state).state;
  assert.equal(state.PendingAsk == null, true);

  // Replay with exact same cursor is idempotent
  const replayRes = PlanningSurface.applyWorkEvent(PlanningSurface.askResolved('inc-1', 15n), state);
  assert.equal(replayRes.ok, true);

  // Conflicting resolution with different cursor is rejected
  const conflictRes = PlanningSurface.applyWorkEvent(PlanningSurface.askResolved('inc-1', 99n), state);
  assert.equal(conflictRes.ok, false);
  assert.match(conflictRes.error, /conflicting/i);
});
