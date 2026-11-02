import test from 'node:test'
import assert from 'node:assert/strict'
import { createServer } from 'node:http'
import { once } from 'node:events'
import { transportRoundTrip } from '../../../dist/OpenCode/Host/SessionsSurface.js'

async function withServer(answer, work) {
  const calls = []
  const server = createServer(async (request, response) => {
    let body = ''
    for await (const chunk of request) body += chunk
    const call = {method: request.method, path: request.url, body: body ? JSON.parse(body) : null}
    calls.push(call)
    const reply = answer(call)
    response.writeHead(reply.status ?? 200, {'content-type': 'application/json'})
    response.end(JSON.stringify(reply.body ?? {}))
  })
  server.listen(0, '127.0.0.1')
  await once(server, 'listening')
  try { await work(`http://127.0.0.1:${server.address().port}`, calls) }
  finally { server.close(); await once(server, 'close') }
}
const successful = call => ({body: call.path === '/session' ? {id: 'created'} : call.path.includes('/message?') ? [] : {}})

test('one HTTP selection supplies sending snapshots and catalog while keeping both close protocols', async () => {
  await withServer(successful, async (url, calls) => {
    const result = await transportRoundTrip({serverUrl: `${url}/`, baseUrl: 'http://127.0.0.1:1'})
    assert.equal(result.available, true)
    assert.deepEqual(result.created, {ok: true, value: 'created'})
    assert.deepEqual(result.messages, {ok: true, value: 0})
    assert.equal(result.sentKind, 'AdmittedWithReceipt')
    assert.equal(result.closed.ok, true); assert.equal(result.removed.ok, true)
    assert.deepEqual(calls.map(({method,path}) => [method,path]), [
      ['POST','/session'], ['GET','/session/ses-transport/message?order=asc'],
      ['POST','/session/ses-transport/prompt_async'], ['POST','/session/ses-transport/abort'], ['DELETE','/session/ses-transport'],
    ])
    assert.deepEqual(calls[0].body, {title: 'transport', agent: 'engineer'})
    assert.deepEqual(calls[2].body, {parts: [{type: 'text', text: 'hello'}], agent: 'engineer'})
  })
})

test('HTTP failures retain string snapshot errors, send verdict and catalog error categories without retry', async () => {
  await withServer(call => call.path === '/session' ? successful(call) : {status: call.method === 'DELETE' ? 404 : 503, body: {message: 'unavailable'}}, async (url, calls) => {
    const result = await transportRoundTrip({baseUrl: url})
    assert.deepEqual(result.messages, {ok: false, error: 'HTTP 503'})
    assert.equal(result.sentKind, 'Fatal')
    assert.equal(result.closed.ok, false)
    assert.equal(result.removed.kind, 'NotFound')
    assert.equal(calls.length, 5)
  })
})

test('SDK selection is shared by all views and missing methods never fall back to HTTP', async () => {
  await withServer(successful, async (url, calls) => {
    const methods = []
    const session = {
      create: async () => { methods.push('create'); return {data: {id: 'sdk-created'}} },
      messages: async () => { methods.push('messages'); return {data: []} },
      promptAsync: async () => { methods.push('prompt'); return {} },
      delete: async () => { methods.push('delete'); return {} },
    }
    const result = await transportRoundTrip({client: {session}, serverUrl: url})
    assert.equal(result.created.value, 'sdk-created'); assert.equal(result.messages.value, 0)
    assert.equal(result.sentKind, 'AdmittedWithReceipt')
    assert.deepEqual(methods, ['create','messages','prompt','delete','delete'])
    assert.equal(calls.length, 0)
    const missing = await transportRoundTrip({client: {session: {}}, serverUrl: url})
    assert.equal(missing.created.ok, false); assert.equal(missing.messages.ok, false)
    assert.equal(missing.removed.ok, false); assert.equal(calls.length, 0)
  })
})
