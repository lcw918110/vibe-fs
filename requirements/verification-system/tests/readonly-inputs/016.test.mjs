import { registerReadonlyInputTests } from '../support/readonly-input-tests.mjs'

process.env.WXS_VERIFICATION_TOOL_DIAGNOSTICS = '1'
registerReadonlyInputTests()
