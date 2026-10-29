import { registerNugetProjectTests } from '../support/nuget-project-tests.mjs'

process.env.WXS_VERIFICATION_TOOL_DIAGNOSTICS = '1'
registerNugetProjectTests()
