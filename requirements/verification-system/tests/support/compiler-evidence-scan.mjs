export function compilerEvidenceReferences(text, { allowCompilerApi = false } = {}) {
  const compilerApi = /FSharp\.Compiler(?:\.Service)?|FSharpChecker|Fable\.(?:Compiler|AST)\.dll/
  const scan = /scanCompilerObservationsV1|scanDslCompilerEvidence|runLocalityDependencyScan|scanProductionLocalitySliceReportV1|locality-symbol-uses/
  return text.split('\n').flatMap((line, index) => {
    const match = scan.exec(line) ?? (allowCompilerApi ? null : compilerApi.exec(line))
    return match ? [{ line: index + 1, token: match[0] }] : []
  })
}
