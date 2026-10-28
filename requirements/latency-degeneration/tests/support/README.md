# Real-host latency-degeneration repro (manual, not part of the suite)

`run-real-host-degeneration.mjs` boots the real OpenCode 1.18.29 with the built plugin and a
mock provider: an orchestrator root commissions a Manager child (a managed session).

- `REPRO_MODE=stream` — the child's provider stream stalls; expects a watchdog interrupt and the
  continuation prompt to reach the provider (`continued: true`).
- `REPRO_MODE=slowtool` — wraps the plugin with `slow-tool-plugin.mjs`; the child's `negotiate`
  blocks `WXS_SLOW_TOOL_MS` inside the provider step. `/tmp/slow-tool-trace.jsonl` records whether
  the tool was aborted. `REPRO_WARMUP=n` first runs n fast tool steps in the child.

The detector has a 100-observation novice protection (no interrupt before it), so an interrupt
needs ≥100 child samples; for a quick local check lower `Seen >= 100` in
`wanxiangli/dist/degeneration/Degeneration.js` temporarily. Diagnostics: `WANXIANGSHU_DIAG=1` is set;
Host stderr is saved to `/tmp/degeneration-real-host.log`, provider bodies to `/tmp/degen-reqs.jsonl`,
Host events to `/tmp/degen-events.jsonl`.
