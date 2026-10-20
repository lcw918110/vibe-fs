import * as fission from '../../../../dist/Execution/Fission/Surface.js'

export { fission }
export const parsed = () => fission.parsePrompt([' lane A  ', 'lane B'])
export const deferred = () => {
  let resolve
  const promise = new Promise(done => { resolve = done })
  return { promise, resolve }
}
export const harness = ({ failCreateAt, failStartAt, failInterrupt = false, parent = 'old-parent', ownerView = 'CANONICAL-OWNER-VIEW', beforeStart = async () => {} } = {}) => {
  const events = []
  let serial = 0
  let startCursor = -1
  const runtime = fission.createAdmission({
    parentOf: async owner => {
      events.push(['parent', owner])
      return parent
    },
    ownerView: async owner => {
      events.push(['owner-view', owner])
      return ownerView
    },
    createLane: async (owner, physicalParent, lane) => {
      events.push(['create', lane.index, physicalParent])
      if (lane.index === failCreateAt) throw new Error(`create-${lane.index}`)
      const session = `lane-${++serial}`
      events.push(['created', session])
      return session
    },
    startLane: async (session, startup) => {
      startCursor += 1
      const index = startCursor
      events.push(['start', index, session, startup])
      await beforeStart(index)
      if (index === failStartAt) throw new Error(`start-${index}`)
      events.push(['started', index])
    },
    abortLane: async session => { events.push(['rollback', session]) },
    silentInterruptOwner: async owner => {
      events.push(['silent-interrupt', owner])
      if (failInterrupt) throw new Error('interrupt-failed')
    },
  })
  return { events, runtime }
}
