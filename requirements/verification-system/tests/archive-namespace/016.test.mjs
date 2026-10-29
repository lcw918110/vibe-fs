import { registerArchiveNamespaceTests } from '../support/archive-namespace-tests.mjs'

process.env.WXS_VERIFICATION_TOOL_DIAGNOSTICS = '1'
registerArchiveNamespaceTests()
