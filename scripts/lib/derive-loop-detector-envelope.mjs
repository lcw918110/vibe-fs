import { mkdirSync, readFileSync, writeFileSync } from 'node:fs'
import os from 'node:os'
import path from 'node:path'
import { fileURLToPath } from 'node:url'
import { Worker } from 'node:worker_threads'
import { encode, vocabularySize } from 'gpt-tokenizer/encoding/o200k_base'
import {
  loopDetectorRepositoryCorpusTexts,
  loopDetectorRepositoryInputFiles,
} from './loop-detector-repository-corpus.mjs'
import {
  buildGeneratedArtifactRowV1,
  canonicalizeSelectedInputPathsV1,
  createTrackingReaderV1,
  readSelectedInputsV1,
} from './generated-artifact-v1.mjs'
import {
  loopDetectorEnvelopeDistPath,
  loopDetectorEnvelopeRepositoryPath,
} from './loop-detector-envelope-paths.mjs'

const defaultRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../..')
const halfLife = 256
// degeneration-guard-003: empirical quantile envelope. Low-side 97.5% confidence (lower quantile p=0.025)
// and high-side 100% (upper quantile p=1.0, maximum corpus value for random anomaly threshold).
const lowerQuantileProbability = 0.025
const upperQuantileProbability = 1.0
const centralProbability = upperQuantileProbability - lowerQuantileProbability

const replayAffine = (tokens, lambda) => {
  if (tokens.length === 0) throw new Error('Loop detector repository corpus has no tokens')

  const lastSeen = new Map()
  const offsets = new Float64Array(tokens.length)
  let coefficient = 1
  let offset = 0
  let coefficientSum = 0
  let offsetSum = 0

  for (let index = 0; index < tokens.length; index += 1) {
    const step = index + 1
    const token = tokens[index]
    const previous = lastSeen.get(token)
    const replacement = 1 - (previous === undefined ? 0 : lambda ** (step - previous))
    coefficient *= lambda
    offset = lambda * offset + replacement
    lastSeen.set(token, step)
    offsets[index] = offset
    coefficientSum += coefficient
    offsetSum += offset
  }

  return { offsets, coefficientSum, offsetSum }
}

const solveNormalPrior = (replay) => {
  const meanCoefficient = replay.coefficientSum / replay.offsets.length
  const meanOffset = replay.offsetSum / replay.offsets.length
  if (!(meanCoefficient < 1)) throw new Error('Loop detector repository corpus has invalid affine coefficient')
  return meanOffset / (1 - meanCoefficient)
}

export const envelopeBounds = (projected, lowerProbability) => {
  if (projected.length === 0) {
    throw new Error('Loop detector envelope has no samples')
  }
  if (!(lowerProbability > 0 && lowerProbability <= 1)) {
    throw new Error('Loop detector envelope has invalid probability')
  }

  const sorted = Float64Array.from(projected).sort()
  const lowerIndex = Math.ceil(lowerProbability * sorted.length) - 1
  return {
    minimum: sorted[lowerIndex],
    maximum: sorted[sorted.length - 1],
  }
}

const evaluateEnvelope = (offsets, lambda, normalPrior) => {
  let coefficient = 1
  const projected = new Float64Array(offsets.length)

  for (let index = 0; index < offsets.length; index += 1) {
    coefficient *= lambda
    projected[index] = coefficient * normalPrior + offsets[index]
  }

  return envelopeBounds(projected, lowerQuantileProbability)
}

// encodeParallel splits the corpus only at safe line-boundary positions (after
// '\n', next byte printable non-'/' ASCII) so chunked encoding is token-identical
// to whole-stream encoding: '\n' otherwise merges into the preceding token.
const safeSplitPosition = (text, position) => {
  if (position === 0 || position >= text.length || text[position - 1] !== '\n') return false

  const code = text.charCodeAt(position)
  return code >= 0x21 && code <= 0x7e && code !== 0x2f
}

const chunkRanges = (text, targetChunkCount) => {
  if (targetChunkCount <= 1 || text.length === 0) return [{ begin: 0, end: text.length }]

  const chunks = []
  let begin = 0

  for (let index = 1; index < targetChunkCount; index += 1) {
    let position = Math.max(begin + 1, Math.trunc((text.length * index) / targetChunkCount))
    while (position < text.length && !safeSplitPosition(text, position)) position += 1
    if (position === text.length) break

    chunks.push({ begin, end: position })
    begin = position
  }

  chunks.push({ begin, end: text.length })
  return chunks
}

const encodeInProcess = (text) => Array.from(encode(text))

const encodeWithWorkerThreads = (text, chunks, threadCount, { workerFactory = (src, opts) => new Worker(src, opts) } = {}) =>
  new Promise((resolve, reject) => {
    const results = new Array(chunks.length)
    let dispatched = 0
    let returned = 0
    let failed = false
    const activeWorkers = new Set()

    const workerSource = `const { parentPort } = require('node:worker_threads')
const { encode } = require('gpt-tokenizer/encoding/o200k_base')
parentPort.on('message', (message) => {
  if (message.done) {
    process.exit(0)
  }
  const tokens = encode(message.text)
  const packed = new Int32Array(tokens)
  parentPort.postMessage({ index: message.index, packed }, [packed.buffer])
})`

    const settle = async (error) => {
      if (failed) return
      if (error) {
        failed = true
        const terminations = []
        for (const worker of activeWorkers) {
          terminations.push(worker.terminate())
        }
        activeWorkers.clear()
        await Promise.all(terminations)
        reject(error)
        return
      }
      if (returned === chunks.length) {
        let total = 0
        for (const packed of results) total += packed.length
        const tokens = new Array(total)
        let cursor = 0
        for (const packed of results) {
          for (let i = 0; i < packed.length; i += 1) {
            tokens[cursor] = packed[i]
            cursor += 1
          }
        }
        resolve(tokens)
      }
    }

    const spawnWorker = () => {
      const worker = workerFactory(workerSource, { eval: true })
      activeWorkers.add(worker)
      worker.unref()
      worker.on('message', (message) => {
        if (failed) return
        results[message.index] = message.packed
        returned += 1
        if (dispatched < chunks.length) {
          const index = dispatched
          dispatched += 1
          const { begin, end } = chunks[index]
          worker.postMessage({ index, text: text.slice(begin, end) })
        } else {
          worker.postMessage({ done: true })
          settle()
        }
      })
      worker.on('error', (error) => {
        settle(error)
      })
      worker.on('exit', (code) => {
        activeWorkers.delete(worker)
        if (code !== 0 && !failed) {
          settle(new Error(`loop detector tokenize worker exited with ${code}`))
        }
      })
      return worker
    }

    const poolSize = Math.min(threadCount, chunks.length)
    for (let workerIndex = 0; workerIndex < poolSize; workerIndex += 1) {
      const worker = spawnWorker()
      const index = dispatched
      dispatched += 1
      const { begin, end } = chunks[index]
      worker.postMessage({ index, text: text.slice(begin, end) })
    }
  })

export const encodeParallel = async (text, workerCount = 0, options = {}) => {
  if (text.length === 0) return []

  if (workerCount === 0) {
    workerCount = Math.max(1, typeof os.availableParallelism === 'function' ? os.availableParallelism() : 1)
  }
  if (workerCount <= 1) return encodeInProcess(text)

  // Split at 8x candidate positions so the pool stays saturated when the
  // corpus has few safe line boundaries; surplus chunks are work-stolen.
  const chunks = chunkRanges(text, workerCount * 8)
  if (chunks.length <= 1) return encodeInProcess(text)

  return encodeWithWorkerThreads(text, chunks, workerCount, options)
}

export const loopDetectorEnvelopeLinkageV1 = Object.freeze({
  import_specifier: '#wanxiangshu-loop-detector-envelope',
  package_import_target: `./${loopDetectorEnvelopeDistPath}`,
  generator_path: 'scripts/lib/derive-loop-detector-envelope.mjs',
  generator_entry: 'writeLoopDetectorEnvelopeArtifact',
  input_selector_path: 'scripts/lib/loop-detector-repository-corpus.mjs',
  input_selector_entry: 'loopDetectorRepositoryInputFiles',
  build_path: 'scripts/build.mjs',
  build_entry: 'verifyArtifacts',
})

export const loadLoopDetectorRepositoryCorpusV1 = (root = defaultRoot, {
  selectInputFiles = loopDetectorRepositoryInputFiles,
  readFile = readFileSync,
} = {}) => {
  const trackingReader = createTrackingReaderV1({ root, readFile })
  const selectedPaths = canonicalizeSelectedInputPathsV1(root, selectInputFiles(root))
  const selectedInputs = readSelectedInputsV1(selectedPaths, trackingReader)
  return {
    selectedInputs: selectedInputs.map(({ path: inputPath, blob_digest: blobDigest }) => ({
      path: inputPath,
      blob_digest: blobDigest,
    })),
    texts: loopDetectorRepositoryCorpusTexts(selectedInputs),
  }
}

const deriveEnvelopeFromCorpus = async ({ selectedInputs, texts }) => {
  const lambda = 2 ** (-1 / halfLife)
  const tokens = await encodeParallel(texts.join('\n'))

  const replay = replayAffine(tokens, lambda)
  const normalPrior = solveNormalPrior(replay)
  const envelope = evaluateEnvelope(replay.offsets, lambda, normalPrior)

  return {
    vocabularySize,
    halfLife,
    lambda,
    centralProbability,
    lowerQuantileProbability,
    upperQuantileProbability,
    normalPrior,
    minimum: envelope.minimum,
    maximum: envelope.maximum,
    corpusTokens: tokens.length,
    selectedInputs,
  }
}

export const deriveLoopDetectorEnvelope = async (root = defaultRoot, dependencies = {}) =>
  deriveEnvelopeFromCorpus(loadLoopDetectorRepositoryCorpusV1(root, dependencies))

const artifactSource = (envelope) => `// auto-generated from the repository SSOT; do not edit by hand.
// Refresh with \`node scripts/derive-envelope.mjs\`; the build copies this tracked artifact to dist.
import { encode } from 'gpt-tokenizer/encoding/o200k_base'
export { encode }

export const vocabularySize = ${envelope.vocabularySize}
export const halfLife = ${envelope.halfLife.toFixed(1)}
export const lambda = ${envelope.lambda.toFixed(16)}
export const centralProbability = ${envelope.centralProbability.toFixed(3)}
export const lowerQuantileProbability = ${envelope.lowerQuantileProbability.toFixed(3)}
export const upperQuantileProbability = ${envelope.upperQuantileProbability.toFixed(3)}
export const normalWeightedDistinctCount = ${envelope.normalPrior.toFixed(14)}
export const minimumWeightedDistinctCount = ${envelope.minimum.toFixed(14)}
export const maximumWeightedDistinctCount = ${envelope.maximum.toFixed(14)}
export const corpusTokens = ${envelope.corpusTokens}
`

const materializeLoopDetectorArtifact = (target, bytes) => {
  mkdirSync(path.dirname(target), { recursive: true })
  writeFileSync(target, bytes)
}

export const writeLoopDetectorEnvelopeArtifact = async (root = defaultRoot, {
  writeArtifact = materializeLoopDetectorArtifact,
  ...deriveDependencies
} = {}) => {
  const envelope = await deriveLoopDetectorEnvelope(root, deriveDependencies)
  const artifactBytes = Buffer.from(artifactSource(envelope), 'utf8')
  writeArtifact(path.join(root, loopDetectorEnvelopeRepositoryPath), artifactBytes)
  return {
    ...envelope,
    generatedArtifact: buildGeneratedArtifactRowV1({
      artifact_path: loopDetectorEnvelopeRepositoryPath,
      artifact_bytes: artifactBytes,
      selected_inputs: envelope.selectedInputs,
      linkage: loopDetectorEnvelopeLinkageV1,
    }),
  }
}
