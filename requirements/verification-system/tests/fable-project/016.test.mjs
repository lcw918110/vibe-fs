import { registerFableProjectTests } from '../support/fable-project-tests.mjs'

process.env.WXS_VERIFICATION_TOOL_DIAGNOSTICS = '1'
registerFableProjectTests()
