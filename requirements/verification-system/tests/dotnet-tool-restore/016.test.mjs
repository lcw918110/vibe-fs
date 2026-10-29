import { registerDotnetToolRestoreTests } from '../support/dotnet-tool-restore-tests.mjs'

process.env.WXS_VERIFICATION_TOOL_DIAGNOSTICS = '1'
registerDotnetToolRestoreTests()
