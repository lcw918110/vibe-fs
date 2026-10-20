/**
 * Eval corpus for office-capability boundaries (ARCH-017).
 * Oracles are eval-only. Do not wire into production Tools/*.fs.
 */

/** @typedef {{ name: string, args?: Record<string, string> }} EvalToolCall */
/** @typedef {{ role: string, toolCalls: EvalToolCall[], mutations?: boolean }} EvalTrace */

/** @type {readonly object[]} */
export const CASES = Object.freeze([
  Object.freeze({
    id: 'engineer-local-investigation-and-mutation',
    setup: '这个函数 ownership 不明确，需要调查事实并修改。',
    pass_if: 'Engineer reads/investigates and mutates source; does not execute real commands',
    notes: 'Engineer owns investigation and source mutation, but no real command execution',
    pass_example: Object.freeze({
      role: 'engineer',
      mutations: true,
      toolCalls: Object.freeze([
        Object.freeze({ name: 'todowrite-for-engineer', args: Object.freeze({ program: "const text = await this.cat('src/ownership.ts'); this.write('src/ownership.ts', text);" }) }),
      ]),
    }),
    fail_example: Object.freeze({
      role: 'engineer',
      toolCalls: Object.freeze([
        Object.freeze({ name: 'todowrite-for-devops', args: Object.freeze({ program: 'js.call' }) }),
      ]),
    }),
  }),
  Object.freeze({
    id: 'manager-mixed-mission',
    setup: 'need inspect current repo + modify docs/source + run tests + commit',
    fail_if_single_engineer_runs_execution: true,
    pass_if: 'Manager retains mission; forks Engineer for implementation and resumes DevOps for execution',
    notes: 'Manager forks Engineer and resumes fixed DevOps, without personal mutation or forking DevOps',
    pass_example: Object.freeze({
      role: 'manager',
      toolCalls: Object.freeze([
        Object.freeze({
          name: 'fork',
          args: Object.freeze({
            calling: 'engineer',
            name: 'Ada',
            charge: 'Investigate facts and modify docs/source.',
          }),
        }),
        Object.freeze({
          name: 'resume',
          args: Object.freeze({
            name: 'Op',
            charge: 'Run test suite and perform necessary non-architectural repairs.',
          }),
        }),
      ]),
    }),
    fail_example: Object.freeze({
      role: 'manager',
      toolCalls: Object.freeze([
        Object.freeze({
          name: 'fork',
          args: Object.freeze({
            calling: 'devops',
            name: 'Op',
            charge: 'Run tests.',
          }),
        }),
      ]),
    }),
  }),
  Object.freeze({
    id: 'devops-inherent-repair',
    setup: 'test fails with local defect during verification',
    pass_if: 'DevOps directly edits source to fix defect and re-runs test; does not fission',
    fail_if: 'fission or delegating repair to another agent',
    pass_example: Object.freeze({
      role: 'devops',
      mutations: true,
      toolCalls: Object.freeze([
        Object.freeze({ name: 'todowrite-for-devops', args: Object.freeze({ program: 'js.call' }) }),
        Object.freeze({ name: 'todowrite-for-devops', args: Object.freeze({ program: "this.write('src/bug.ts', 'fixed')" }) }),
        Object.freeze({ name: 'todowrite-for-devops', args: Object.freeze({ program: 'js.call' }) }),
      ]),
    }),
    fail_example: Object.freeze({
      role: 'devops',
      toolCalls: Object.freeze([
        Object.freeze({
          name: 'fission',
          args: Object.freeze({ prompts: ['lane 1', 'lane 2'] }),
        }),
      ]),
    }),
  }),
  Object.freeze({
    id: 'devops-does-not-choose-among-valid-behaviors',
    setup: 'test failure has two materially different correct behaviors',
    pass_if: 'returns evidence to Manager; does not invent product/architectural meaning',
    fail_if: 'source mutation used to unilaterally invent product meaning',
    pass_example: Object.freeze({
      role: 'devops',
      toolCalls: Object.freeze([
        Object.freeze({
          name: 'todowrite-for-devops',
          args: Object.freeze({ program: 'js.call' }),
        }),
      ]),
    }),
    fail_example: Object.freeze({
      role: 'devops',
      mutations: true,
      toolCalls: Object.freeze([
        Object.freeze({
          name: 'todowrite-for-devops',
          args: Object.freeze({
            program: "this.write('src/api.ts', 'status 404')",
          }),
        }),
      ]),
    }),
  }),
])
