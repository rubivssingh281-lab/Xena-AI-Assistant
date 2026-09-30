# Xena Architecture

Xena currently has a Tkinter desktop shell in `Xena.py`. The orchestration boundary is now separated into the standard-library-only `xena_core` package.

## Core flow

1. A caller builds a `TaskPlan` containing ordered `WorkflowStep` values.
2. `ToolRegistry` resolves each declared tool and exposes capability metadata.
3. `AutomationEngine` enforces a maximum step count, risk approval policy, bounded retries, and verification.
4. `TaskStore` persists task state, task events, approval requests, and artifact records in SQLite.
5. `AutomationScheduler` supports local recurring interval callbacks without blocking the GUI.

## Security boundaries

Filesystem tools are restricted to a configured root and reject traversal. No arbitrary shell tool is registered. Medium/high-risk tools can require approval before execution. Tool results must explicitly report `verified: true` when verification is enabled.

The existing GUI, speech, browser, system, and DeepFace integrations remain in `Xena.py`; they are not yet migrated into the registry because several of them perform external or destructive actions and need individual permission policies.
