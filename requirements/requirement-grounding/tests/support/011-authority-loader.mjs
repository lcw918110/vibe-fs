import assert from 'node:assert/strict'

export async function load(url, context, nextLoad) {
  const loaded = await nextLoad(url, context)
  if (!url.endsWith('/dist/OpenCode/Tools/ToolRegistry.js')) return loaded
  const source = String(loaded.source)
  const admission = 'officeAdmission(ctx, role_1)'
  assert.equal(source.split(admission).length - 1, 1, 'mutation identifies the production role admission decision')
  return {
    ...loaded,
    source: source.replace(admission, '(spec.Name === "js-engineer" ? true : officeAdmission(ctx, role_1))'),
  }
}
