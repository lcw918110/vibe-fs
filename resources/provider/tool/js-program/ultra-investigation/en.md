class Js extends JsProgram {
  async run() {
    const found = await this.rg(["\\b(?:type|module)\\s+RetryPolicy\\b", "src", "-g", "*.fs"]);
    const hits = found.messages.flatMap((message) => {
      if (message.type !== "match") return [];
      const text = message.data.lines.text;
      return [{
        path: message.data.path.text,
        line: message.data.line_number,
        excerpt: text.slice(0, 900),
      }];
    });
    return { declarations: hits, changedFiles: [], executed: false };
  }
}
