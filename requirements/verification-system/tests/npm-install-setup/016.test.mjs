import { registerNpmInstallSetupTests } from '../support/npm-install-setup-tests.mjs'

process.env.WXS_VERIFICATION_TOOL_DIAGNOSTICS = '1'
registerNpmInstallSetupTests()
