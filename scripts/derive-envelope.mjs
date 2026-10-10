#!/usr/bin/env node
// Manual derivation entry for the loop detector repository envelope
// (degeneration-guard-004). The build never derives this artifact; it copies
// the tracked repository artifact into dist. Refresh the repository artifact
// with this command when the tracked corpus changes.
import { writeLoopDetectorEnvelopeArtifact } from './lib/derive-loop-detector-envelope.mjs'
import { loopDetectorEnvelopeDistPath } from './lib/loop-detector-envelope-paths.mjs'

const result = await writeLoopDetectorEnvelopeArtifact()
console.log(`derived ${result.generatedArtifact.artifact_path} (build copies it to ${loopDetectorEnvelopeDistPath})`)
console.log(`corpus tokens: ${result.corpusTokens}`)
console.log(`normal prior: ${result.normalPrior.toFixed(6)}`)
console.log(`minimum: ${result.minimum.toFixed(6)}`)
console.log(`maximum: ${result.maximum.toFixed(6)}`)
