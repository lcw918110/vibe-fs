// Wraps the production plugin; `negotiate` with SLOW-TOOL in its questions blocks for
// WXS_SLOW_TOOL_MS inside the provider step, like a long bash/join/consult tool.
import { pathToFileURL } from 'node:url'
import { appendFileSync } from 'node:fs'
const trace = (record) => { if (process.env.WXS_SLOW_TOOL_TRACE) appendFileSync(process.env.WXS_SLOW_TOOL_TRACE, JSON.stringify({ t: Date.now(), ...record }) + '\n') }

const { default: production } = await import(pathToFileURL(process.env.WXS_SLOW_TOOL_PLUGIN).href)
const delay = Number(process.env.WXS_SLOW_TOOL_MS ?? 40000)
const honorAbort = process.env.WXS_SLOW_TOOL_HONOR_ABORT !== '0'

export default {
  id: 'wanxiangshu-slow-tool-canary',
  async server(input) {
    const hooks = await production.server(input)
    const wrap = (name) => ({
      ...hooks.tool[name],
      execute: async (args, context) => {
        if (JSON.stringify(args).includes('SLOW-TOOL')) {
          trace({ operation: 'slow-tool-start', tool: name, session: context.sessionID })
          await new Promise((resolve) => {
            const timer = setTimeout(resolve, delay)
            if (honorAbort) context.abort?.addEventListener?.('abort', () => { clearTimeout(timer); resolve() }, { once: true })
          })
          trace({ operation: 'slow-tool-end', tool: name, session: context.sessionID, aborted: !!context.abort?.aborted })
          if (context.abort?.aborted) throw new Error('slow tool aborted')
        }
        return hooks.tool[name].execute(args, context)
      },
    })
    return { ...hooks, tool: { ...hooks.tool, negotiate: wrap('negotiate') } }
  },
}
