import test from 'node:test'

{
const { default: assert } = await import("node:assert/strict");
const { default: test } = await import("node:test");
const { assertJsData, assertOpaque } = await import("../../verification-system/tests/support/js-contract.mjs");

const fission = await import('../../../dist/Execution/Fission/Surface.js')
const fissionHost = await import('../../../dist/OpenCode/Host/FissionHostSurface.js')
const parsed = () => fission.parsePrompt([' lane A  ', 'lane B'])
const harness = ({ failCreateAt, failStartAt, failInterrupt = false, parent = 'old-parent' } = {}) => {
  const events = []
  let serial = 0
  let startCursor = -1
  const runtime = fission.createAdmission({
    parentOf: async (owner) => {
      events.push(['parent', owner])
      return parent
    },
    ownerView: async (owner) => {
      events.push(['lwr', owner])
      return 'CANONICAL-OWNER-VIEW'
    },
    createLane: async (_owner, physicalParent, lane) => {
      events.push(['create', lane.index, physicalParent])
      if (lane.index === failCreateAt) throw new Error(`create-${lane.index}`)
      serial += 1
      return `lane-${serial}`
    },
    startLane: async (laneSession, startup) => {
      startCursor += 1
      const index = startCursor
      events.push(['start', index, laneSession, startup])
      if (index === failStartAt) throw new Error(`start-${index}`)
    },
    abortLane: async (laneSession) => {
      events.push(['rollback', laneSession])
    },
    silentInterruptOwner: async (owner) => {
      events.push(['silent-interrupt', owner])
      if (failInterrupt) throw new Error('interrupt-failed')
    },
  })
  return { events, runtime }
}

test('WHAT[intra-participant-parallelism-013] user-facing root caller is rejected before fission reserves or creates anything', async () => {
  const { events, runtime } = harness({ parent: null })
  const owner = 'root-caller'
  const result = await fission.admit(runtime, owner, parsed())

  assert.equal(result.ok, false)
  assert.equal(result.reason, 'InvalidOrigin')
  assert.deepEqual(events, [['parent', 'root-caller']])
  assert.equal(fission.isActive(runtime, owner), false)
})
test('WHAT[intra-participant-parallelism-013] root provider request suppresses fission while a subsession inherits office entitlement', () => {
  assert.deepEqual(
    fissionHost.projectFissionToolVisibility(false, { fork: true, fission: true }),
    { fork: true, fission: false },
  )
  assert.deepEqual(
    fissionHost.projectFissionToolVisibility(true, { fork: true }),
    { fork: true },
  )
})
}

{
const { default: assert } = await import("node:assert/strict");
const { mkdirSync, mkdtempSync, rmSync, writeFileSync } = await import("node:fs");
const { tmpdir } = await import("node:os");
const { join } = await import("node:path");
const { default: test } = await import("node:test");
const { acceptAuthorityRoot, bindManagedChild, grantWorkOwned, withExecutablePlugin } = await import("../../verification-system/tests/support/plugin-fixture.mjs");

const withRoutingHome = async (body) => {
  const previousHome = process.env.HOME
  const root = mkdtempSync(join(tmpdir(), 'wxs-fission-origin-home-'))
  const home = join(root, 'home')
  const configDir = join(home, '.config', 'opencode')
  mkdirSync(configDir, { recursive: true })
  writeFileSync(
    join(configDir, 'wanxiangshu.mjs'),
    "export const routingProtocol = 2\nexport default function route(role, running, previous, purpose) { return { model: 'fixture/root-model', reasoning: 'none' } }\n",
    'utf8',
  )
  process.env.HOME = home

  try {
    await body()
  } finally {
    process.env.HOME = previousHome
    rmSync(root, { recursive: true, force: true })
  }
}

test('WHAT[intra-participant-parallelism-013] real root chat message carries a request-local fission deny', async () => {
  await withRoutingHome(async () => {
    await withExecutablePlugin(async (hooks) => {
      const sessionID = 'fission-root-provider-surface'
      const output = {
        message: {
          id: 'msg-fission-root-provider-surface',
          role: 'user',
          sessionID,
          agent: 'manager',
          model: { providerID: 'host', modelID: 'placeholder' },
          tools: { fork: true, join: true, horizon: true, deliver: true },
        },
        parts: [{ type: 'text', text: 'root work' }],
      }

      await hooks['chat.message']({ sessionID, agent: 'manager' }, output)

      assert.equal(output.message.tools?.fission, false)
      assert.equal(output.message.tools?.fork, true)
      assert.equal(output.message.tools?.join, true)
      assert.equal(output.message.tools?.horizon, true)
      assert.equal(output.message.tools?.deliver, true)
    })
  })
})
test('WHAT[intra-participant-parallelism-013] a durably linked child retains fission', async () => {
  await withExecutablePlugin(async (hooks, _directory, _createdIds, runtime) => {
    const sessionID = 'fission-bound-child-provider-surface'
    await bindManagedChild(runtime, 'fission-binding-parent', sessionID, 'engineer')
    const output = {
      message: {
        id: 'msg-fission-bound-child-provider-surface',
        role: 'user',
        sessionID,
        agent: 'engineer',
        model: { providerID: 'host', modelID: 'placeholder' },
        tools: { fork: true, fission: true },
      },
      parts: [{ type: 'text', text: 'Interrupt the active join.' }],
    }

    await hooks['chat.message']({ sessionID, agent: 'engineer' }, output)
    assert.equal(output.message.tools.fission, true)
    assert.equal(output.message.tools.fork, true)
  })
})
test('WHAT[intra-participant-parallelism-013] forced root fission rejects origin before parsing prompts', async () => {
  await withExecutablePlugin(async (hooks, _directory, _createdIds, runtime) => {
    const sessionID = 'fission-root-origin'
    await acceptAuthorityRoot(runtime, sessionID, 'engineer')
    await grantWorkOwned(runtime, sessionID)

    const result = await hooks.tool.fission.execute(
      { prompts: 'only one lane' },
      {
        sessionID,
        agent: 'engineer',
        callID: 'call-root-fission',
        messageID: 'run-root-fission',
      },
    )

    assert.match(result, /user-facing\/root/i)
    assert.doesNotMatch(result, /at least two|至少需要两条/i)
  })
})
}
