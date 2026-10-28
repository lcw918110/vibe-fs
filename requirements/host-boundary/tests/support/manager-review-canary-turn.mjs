import { isScopeReminderText } from '../../../verification-system/tests/e2e/support/runtime-key.js';

export function isCanaryTurnSettled({ sessionID, messageID, status, messages }) {
  if (status !== undefined && status?.type !== 'idle') return false;
  return messages.some(({ info }) => info?.sessionID === sessionID && info?.role === 'assistant'
    && info?.parentID === messageID && info?.finish === 'stop'
    && typeof info?.time?.completed === 'number' && info?.error === undefined);
}

export function managerReviewStage(body) {
  const prompts = new Map([
    ['READ_SAMPLE', [1, 2, 'call_read_norm_1']],
    ['TRIGGER_ERROR', [4, 5, 'call_read_err_1']],
    ['TRIGGER_EXECUTOR_THROW', [8, 9, 'call_exec_throw_1']],
    ['TRIGGER_CANCEL', [6, 6, null]],
    ['VERIFY_CANCEL_HISTORY', [7, 7, null]],
  ]);
  const messages = Array.isArray(body?.messages) ? body.messages : [];
  for (let index = messages.length - 1; index >= 0; index -= 1) {
    const message = messages[index];
    if (message.role !== 'user') continue;
    if (typeof message.content !== 'string') return null;
    // 标记在 7bf520242 简化为只含锚点编号；保留旧格式兼容，避免解析停在 null。
    const content = message.content.replace(/^\[ANCHOR: \d+(?:; prefixCount: \d+)?\]\u0000\ufeff/, '');
    const prompt = content.split('\u0000')[0];
    if (isScopeReminderText(prompt)) continue;
    const stages = prompts.get(prompt);
    if (!stages) return null;
    const [beforeCall, afterCall, callID] = stages;
    const executed = callID !== null && messages.slice(index + 1).some((row) =>
      (row.tool_calls ?? []).some((call) => call.id === callID));
    return executed ? afterCall : beforeCall;
  }
  return null;
}
