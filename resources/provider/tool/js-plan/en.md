# Tool · js-plan

JavaScript programming surface for inspecting and editing the sole planning artifact P.

## Contract and Usage

1. **Sole Path**: The scope of this tool is strictly bound to the single `plan.md` file for the current task; accessing repository code or external directories is forbidden.
2. **Capability Primitives**: Provides `read()` to inspect full content, `edit(patches)` for precise patches, and `rewrite(content)` for full replacement.
3. **Atomic Publication**: Any write operation is executed via an exclusive temporary file and published by atomic replace after flush; success takes effect immediately.
4. **No Ambient Permissions**: The sandbox does not expose ambient network or process capabilities, remaining strictly controlled.
