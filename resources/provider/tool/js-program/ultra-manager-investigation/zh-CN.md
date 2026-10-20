class Js extends JsProgram {
  async run() {
    const found = await this.rg(["describe\\(", "tests", "-g", "*.test.mjs"]);
    const suites = found.messages.filter((message) => message.type === "match");
    const files = await this.rg(["--files", "-g", "*.test.mjs", "tests"]);
    const packageText = await this.cat("package.json");
    return {
      testFileCount: files.paths.length,
      testSuiteCount: suites.length,
      hasScripts: packageText.includes('"scripts"'),
    };
  }
}