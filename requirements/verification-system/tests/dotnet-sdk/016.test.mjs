import { registerDotnetSdkTests } from '../support/dotnet-sdk-tests.mjs'

process.env.WXS_VERIFICATION_TOOL_DIAGNOSTICS = '1'
registerDotnetSdkTests()
