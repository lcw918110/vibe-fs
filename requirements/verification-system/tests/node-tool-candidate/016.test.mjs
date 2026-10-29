import { registerNodeToolCandidateTests } from '../support/node-tool-candidate-tests.mjs'

process.env.WXS_VERIFICATION_TOOL_DIAGNOSTICS = '1'
registerNodeToolCandidateTests()
