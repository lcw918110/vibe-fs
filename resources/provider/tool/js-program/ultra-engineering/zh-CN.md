class Js extends JsProgram {
  async run() {
    const found = await this.rg(["oldApi", "src", "tests", "-g", "*.js", "-g", "*.ts"]);
    const paths = [...new Set(found.messages.flatMap((message) =>
      message.type === "match" ? [message.data.path.text] : []))];
    let rewritten = 0;
    for (const path of paths) {
      const text = await this.cat(path);
      const spans = this.whereAll(text, "oldApi");
      if (spans.length === 0) continue;
      let next = text;
      for (const [begin, end] of [...spans].reverse()) {
        next = next.slice(0, begin) + "newApi" + next.slice(end);
      }
      this.write(path, next);
      rewritten += spans.length;
    }
    const api = await this.cat("src/api.js");
    const [registryAt] = await this.where(api, 'registry.register("oldApi", oldApi);');
    return { migrated: "oldApi -> newApi", rewritten, registryAt };
  }
}
