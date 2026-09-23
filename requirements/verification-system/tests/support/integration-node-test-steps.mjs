import path from 'node:path'

import { PROJECT_CHECK_TIMEOUT_MS } from '../e2e/support/time-budget.js'

export function integrationNodeTestSteps(root) {
  return [
    // 1. requirements-adapter: 适配层与插件契约、工作树与持久化等日常测试
    {
      label: 'requirements-adapter',
      files: [
        path.join(root, 'requirements/behavior-diagnosis/tests/001.test.mjs'),
        path.join(root, 'requirements/behavior-diagnosis/tests/002.test.mjs'),
        path.join(root, 'requirements/behavior-diagnosis/tests/004.test.mjs'),
        path.join(root, 'requirements/behavior-diagnosis/tests/005.test.mjs'),
        path.join(root, 'requirements/capability-enforcement/tests/001.test.mjs'),
        path.join(root, 'requirements/capability-enforcement/tests/006.test.mjs'),
        path.join(root, 'requirements/capability-enforcement/tests/010.test.mjs'),
        path.join(root, 'requirements/capability-enforcement/tests/011.test.mjs'),
        path.join(root, 'requirements/capability-enforcement/tests/022.test.mjs'),
        path.join(root, 'requirements/change-integration/tests/002.test.mjs'),
        path.join(root, 'requirements/change-integration/tests/005.test.mjs'),
        path.join(root, 'requirements/change-integration/tests/008.test.mjs'),
        path.join(root, 'requirements/cognitive-environment/tests/001.test.mjs'),
        path.join(root, 'requirements/cognitive-environment/tests/002.test.mjs'),
        path.join(root, 'requirements/cognitive-environment/tests/003.test.mjs'),
        path.join(root, 'requirements/cognitive-environment/tests/004.test.mjs'),
        path.join(root, 'requirements/cognitive-environment/tests/005.test.mjs'),
        path.join(root, 'requirements/cognitive-environment/tests/006.test.mjs'),
        path.join(root, 'requirements/durable-convergence/tests/008.test.mjs'),
        path.join(root, 'requirements/durable-convergence/tests/009.test.mjs'),
        path.join(root, 'requirements/durable-events/tests/003.test.mjs'),
        path.join(root, 'requirements/durable-events/tests/009.test.mjs'),
        path.join(root, 'requirements/managed-chat-execution/tests/009.test.mjs'),
        path.join(root, 'requirements/repository-programming/tests/020.test.mjs'),
        path.join(root, 'requirements/speculative-investigation/tests/008.test.mjs'),
        path.join(root, 'requirements/crash-reconciliation/tests/020.test.mjs'),
        path.join(root, 'requirements/epistemic-reasoning/tests/036.test.mjs'),
        path.join(root, 'requirements/office-capability/tests/005.test.mjs'),
        path.join(root, 'requirements/office-capability/tests/007.test.mjs'),
        path.join(root, 'requirements/office-capability/tests/016.test.mjs'),
        path.join(root, 'requirements/office-capability/tests/017.test.mjs'),
      ],
    },
    // 2. compiler-canary: 编译边界与影响分析 CLI，耗时较长（releaseOnly: true）
    {
      label: 'compiler-canary',
      files: [
        path.join(root, 'requirements/structured-workflow/tests/012.test.mjs'),
      ],
      perTestTimeoutMs: PROJECT_CHECK_TIMEOUT_MS,
      releaseOnly: true,
    },
    // 3. repository-envelope: 仓库退化守卫 envelope（releaseOnly: true）
    {
      label: 'repository-envelope',
      files: [
        path.join(root, 'requirements/degeneration-guard/tests/004.test.mjs'),
      ],
      releaseOnly: true,
    },
  ]
}

export function selectIntegrationSteps(root, { releaseOnly = false } = {}) {
  return integrationNodeTestSteps(root).filter((step) => releaseOnly || !step.releaseOnly)
}
