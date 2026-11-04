import { buildToolCallChunks, buildToolCallsChunks, buildTextChunks, sendJSON } from './strict-mock-sse.js';
import { decorateLegacyArgs, decorateScopeArgs } from './strict-mock-decorate.js';

const MOCK_MODEL = 'mock';

// Provider metadata only: this is deliberately independent of context-limit logic.
const estimatePromptTokens = (body) =>
  Math.max(1, Math.ceil(JSON.stringify(body?.messages || []).length / 2));
const SSE_HEADERS = {
  'Content-Type': 'text/event-stream',
  'Cache-Control': 'no-cache',
  'Connection': 'keep-alive',
};

const awaitResponseClose = (res) =>
  new Promise((resolve) => {
    if (res.destroyed || res.writableEnded) {
      resolve();
      return;
    }
    res.once('close', resolve);
  });

async function respondStream(res, chunks, exp) {
  res.writeHead(200, { ...SSE_HEADERS, 'X-Accel-Buffering': 'no' });

  if (exp.respond.missingUsage) {
    for (const chunk of chunks) delete chunk.usage;
  }

  for (let index = 0; index < chunks.length; index++) {
    if (index === 0 && exp.respond.waitFirstTokenUntil != null) {
      await exp.respond.waitFirstTokenUntil;
    }
    if (index === 0 && exp.respond.delayFirstToken > 0) {
      await new Promise((resolve) => setTimeout(resolve, exp.respond.delayFirstToken));
    }
    if (exp.respond.disconnectMidSse && index === Math.floor(chunks.length / 2)) {
      res.destroy();
      return;
    }
    res.write(`data: ${JSON.stringify(chunks[index])}\n\n`);
    // Causal stream hold for Long Stroke control-sentinel canaries. The first
    // reasoning delta is physically visible; the only success-path release is
    // the provider HTTP response closing because OpenCode cancelled that attempt.
    // No fixed sleep and no dependency on a later consultation request.
    if (index === 0 && exp.respond.waitForAbortAfterFirstToken === true) {
      await awaitResponseClose(res);
      return;
    }
    if (index === 0 && exp.respond.waitAfterFirstTokenUntil != null) {
      await Promise.race([exp.respond.waitAfterFirstTokenUntil, awaitResponseClose(res)]);
      if (res.destroyed || res.writableEnded) return;
    }
    if (exp.respond.yieldBetweenChunks && index < chunks.length - 1) {
      await new Promise((resolve) => setImmediate(resolve));
    }
  }

  if (exp.respond.neverEnd) return;
  // Optional causal hold: keep the SSE open until a Promise resolves (no fixed sleep).
  // Used by manager-unhappy-path so C1 stays incomplete across join + user_message wake.
  if (exp.respond.waitUntil != null) {
    await exp.respond.waitUntil;
  }
  if (exp.respond.delayDone > 0) {
    await new Promise((resolve) => setTimeout(resolve, exp.respond.delayDone));
  }
  res.write('data: [DONE]\n\n');
  res.end();
}

function toolArgs(exp, parsed, strict, state, toolName, position = 0) {
  const args = typeof exp.respond.args === 'function'
    ? exp.respond.args(parsed)
    : { ...exp.respond.args };
  if (typeof state?.rewriteToolArgs === 'function') {
    const rewritten = state.rewriteToolArgs(exp, args, parsed);
    if (rewritten && typeof rewritten === 'object') Object.assign(args, rewritten);
  }
  if (!strict) decorateLegacyArgs(args);
  return decorateScopeArgs(args, state.retention, toolName, position);
}

function withPrefixText(chunks, text) {
  if (typeof text !== 'string' || text.length === 0 || chunks.length === 0) return chunks;
  const first = chunks[0];
  const choice = first?.choices?.[0];
  if (!choice?.delta) return chunks;
  return [
    {
      ...first,
      choices: [{ ...choice, delta: { ...choice.delta, content: text } }],
    },
    ...chunks.slice(1),
  ];
}

function toolCallChunks(id, exp, parsed, strict, promptTokens, state) {
  const args = toolArgs(exp, parsed, strict, state, exp.respond.tool, 0);
  const argsText = exp.respond.malformedArgs
    ? '{malformed_json_arguments:'
    : JSON.stringify(args);

  if (exp.respond.toolCallAsText) {
    return buildTextChunks(id, `call tool ${exp.respond.tool} with args ${argsText}`, promptTokens);
  }
  const chunks = exp.respond.fragmentArgs
    ? fragmentedToolCallChunks(id, exp, argsText, promptTokens)
    : exp.respond.duplicateToolCallId
      ? duplicateToolCallChunks(id, exp, argsText, promptTokens)
      : buildToolCallChunks(id, exp.respond.tool, argsText, promptTokens);
  return withPrefixText(chunks, exp.respond.prefixText);
}

function multiToolCallChunks(id, exp, parsed, strict, promptTokens, state) {
  const calls = (exp.respond.calls ?? []).map((call, position) => {
    const callExp = { ...exp, respond: { ...exp.respond, ...call, type: 'tool-call' } };
    return { name: call.tool, argsStr: JSON.stringify(toolArgs(callExp, parsed, strict, state, call.tool, position)) };
  });
  if (!exp.respond.streamCalls) {
    return withPrefixText(buildToolCallsChunks(id, calls, promptTokens), exp.respond.prefixText);
  }

  return withPrefixText([
    { id, object: 'chat.completion.chunk', created: 1, model: MOCK_MODEL, choices: [{ index: 0, delta: { role: 'assistant', content: null }, finish_reason: null }] },
    ...calls.map((call, index) => ({
      id,
      object: 'chat.completion.chunk',
      created: 1,
      model: MOCK_MODEL,
      choices: [{
        index: 0,
        delta: {
          tool_calls: [{
            index,
            id: `${id}_${index + 1}`,
            type: 'function',
            function: { name: call.name, arguments: call.argsStr },
          }],
        },
        finish_reason: null,
      }],
    })),
    finishedToolCallChunk(id, promptTokens),
  ], exp.respond.prefixText);
}

function fragmentedToolCallChunks(id, exp, argsText, promptTokens) {
  const chunks = [{
    id,
    object: 'chat.completion.chunk',
    created: 1,
    model: MOCK_MODEL,
    choices: [{ index: 0, delta: { role: 'assistant', content: null }, finish_reason: null }],
  }];
  const fragmentSize = exp.respond.fragmentSize || 3;
  for (let index = 0; index < argsText.length; index += fragmentSize) {
    chunks.push({
      id,
      object: 'chat.completion.chunk',
      created: 1,
      model: MOCK_MODEL,
      choices: [{
        index: 0,
        delta: {
          tool_calls: [{
            index: 0,
            id,
            type: 'function',
            function: {
              name: index === 0 ? exp.respond.tool : undefined,
              arguments: argsText.slice(index, index + fragmentSize),
            },
          }],
        },
        finish_reason: null,
      }],
    });
  }
  chunks.push(finishedToolCallChunk(id, promptTokens));
  return chunks;
}

function duplicateToolCallChunks(id, exp, argsText, promptTokens) {
  const toolCall = {
    index: 0,
    id,
    type: 'function',
    function: { name: exp.respond.tool, arguments: argsText },
  };
  return [
    { id, object: 'chat.completion.chunk', created: 1, model: MOCK_MODEL, choices: [{ index: 0, delta: { role: 'assistant', content: null }, finish_reason: null }] },
    { id, object: 'chat.completion.chunk', created: 1, model: MOCK_MODEL, choices: [{ index: 0, delta: { tool_calls: [toolCall] }, finish_reason: null }] },
    { id, object: 'chat.completion.chunk', created: 1, model: MOCK_MODEL, choices: [{ index: 0, delta: { tool_calls: [toolCall] }, finish_reason: null }] },
    finishedToolCallChunk(id, promptTokens),
  ];
}

function finishedToolCallChunk(id, promptTokens) {
  return {
    id,
    object: 'chat.completion.chunk',
    created: 1,
    model: MOCK_MODEL,
    choices: [{ index: 0, delta: {}, finish_reason: 'tool_calls' }],
    usage: { prompt_tokens: promptTokens, completion_tokens: 100, total_tokens: promptTokens + 100 },
  };
}

function reasoningOnlyChunks(id, exp, promptTokens) {
  const text = typeof exp.respond.reasoningOnly === 'string' ? exp.respond.reasoningOnly : 'thinking...';
  return [
    { id, object: 'chat.completion.chunk', created: 1, model: MOCK_MODEL, choices: [{ index: 0, delta: { role: 'assistant', reasoning_content: text }, finish_reason: null }] },
    { id, object: 'chat.completion.chunk', created: 1, model: MOCK_MODEL, choices: [{ index: 0, delta: {}, finish_reason: 'stop' }], usage: { prompt_tokens: promptTokens, completion_tokens: 100, total_tokens: promptTokens + 100 } },
  ];
}

export async function respond(state, res, exp, parsed) {
  const id = exp.respond.type === 'title'
    ? `title_${++state.responseCounter}`
    : `call_${++state.responseCounter}`;
  const promptTokens = estimatePromptTokens(parsed);

  if (exp.respond.type === 'error') {
    return sendJSON(res, exp.respond.status || 500, exp.respond.body || { error: 'mock error' }, exp.respond.headers);
  }
  if (exp.respond.contextOverflow) {
    return sendJSON(res, 400, {
      error: {
        message: "This model's maximum context length is 100000 tokens.",
        type: 'invalid_request_error',
        code: 'context_length_exceeded',
      },
    });
  }
  if (exp.respond.type === 'disconnect') return respondDisconnect(res, id);

  const chunks = exp.respond.type === 'tool-call'
    ? toolCallChunks(id, exp, parsed, state.strict, promptTokens, state)
    : exp.respond.type === 'tool-calls'
      ? multiToolCallChunks(id, exp, parsed, state.strict, promptTokens, state)
    : exp.respond.emptyAssistant
      ? buildTextChunks(id, '', promptTokens)
      : exp.respond.reasoningOnly
        ? reasoningOnlyChunks(id, exp, promptTokens)
        : buildTextChunks(id, exp.respond.text, promptTokens);

  if (exp.respond.errorAfterToolCall) {
    res.writeHead(200, { ...SSE_HEADERS, 'X-Accel-Buffering': 'no' });
    res.write(`data: ${JSON.stringify(chunks[0])}\n\n`);
    res.destroy();
    return;
  }
  return respondStream(res, chunks, exp);
}

function respondDisconnect(res, id) {
  res.writeHead(200, SSE_HEADERS);
  res.write(`data: ${JSON.stringify({
    id,
    object: 'chat.completion.chunk',
    created: 1,
    model: MOCK_MODEL,
    choices: [{ index: 0, delta: { role: 'assistant' }, finish_reason: null }],
  })}\n\n`);
  res.destroy();
}
