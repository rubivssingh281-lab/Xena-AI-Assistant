# Tools

Tools are registered with `ToolRegistry` using a name, description, risk level, and handler. The default registry currently provides:

- `filesystem.list` (low risk)
- `filesystem.read_text` (low risk)
- `filesystem.write_text` (medium risk)

A new tool should provide validated arguments, a real handler, a declared risk level, and a result containing `verified: true` when the requested state has been checked. Shell execution, browser control, messaging, and destructive system operations are intentionally not registered in this core until they have dedicated permission and audit policies.
