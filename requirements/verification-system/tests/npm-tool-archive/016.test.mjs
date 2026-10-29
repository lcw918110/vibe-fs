import { registerNpmToolArchiveTests } from '../support/npm-tool-archive-tests.mjs'

process.env.WXS_VERIFICATION_TOOL_DIAGNOSTICS = '1'
registerNpmToolArchiveTests()
