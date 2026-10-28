import fs from 'node:fs'
import path from 'node:path'

export function createUsage(file) {
  let data
  let disabled = false
  function save(change) {
    if (disabled) return false
    try {
      if (!data) {
        try { data = JSON.parse(fs.readFileSync(file, 'utf8')) }
        catch (error) {
          if (error.code !== 'ENOENT') throw error
          data = { version: 1, totals: { attempts: 0, success: 0, failure: 0, unknown: 0,
            created: 0, reused: 0, reusedAcrossBuilds: 0, missingReuseReports: 0 }, samples: [] }
        }
        if (data.version !== 1 || !Array.isArray(data.samples) || !data.totals
          || ['attempts', 'success', 'failure', 'unknown', 'created', 'reused', 'reusedAcrossBuilds', 'missingReuseReports']
            .some(key => !Number.isSafeInteger(data.totals[key]) || data.totals[key] < 0)) {
          throw new Error('Invalid usage record')
        }
        const pending = data.samples.at(-1)
        if (pending?.outcome === 'pending') {
          pending.outcome = 'unknown'
          data.totals.unknown++
          data.totals.missingReuseReports++
        }
      }
      change()
      fs.mkdirSync(path.dirname(file), { recursive: true })
      const temporary = file + '.tmp'
      fs.writeFileSync(temporary, JSON.stringify(data) + '\n', { mode: 0o600 })
      fs.renameSync(temporary, file)
      return true
    } catch (error) {
      disabled = true
      console.error(`Build usage recording disabled: ${error.message}`)
      return false
    }
  }
  return {
    begin(details) {
      const startedAt = Date.now()
      const started = performance.now()
      let sample
      if (!save(() => {
        const previous = data.samples.at(-1)
        const elapsed = time => time != null && startedAt >= time ? startedAt - time : null
        sample = { startedAt, intervalMs: elapsed(previous?.startedAt), idleMs: elapsed(previous?.finishedAt),
          ...details, outcome: 'pending' }
        data.totals.attempts++
        data.samples.push(sample)
        if (data.samples.length > 256) data.samples.shift()
      })) return () => {}
      return result => save(() => {
        Object.assign(sample, result, { finishedAt: Date.now(), durationMs: performance.now() - started })
        data.totals[result.outcome]++
        if (result.reuse) {
          for (const key of ['created', 'reused', 'reusedAcrossBuilds']) data.totals[key] += result.reuse[key]
        } else data.totals.missingReuseReports++
      })
    },
  }
}
