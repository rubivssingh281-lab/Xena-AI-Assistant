# Security

The automation core applies these controls:

- Filesystem paths are resolved and checked against `~/XenaAutomation`.
- No arbitrary command execution is exposed through the core registry.
- Tool risk is explicit and approval policies are injectable.
- Plans have a maximum step count and retries are bounded.
- Tool results must be verified before a verified step is accepted.
- Task events and approval requests are persisted in SQLite.

The legacy assistant still contains powerful features such as terminal command launch, shutdown/restart, browser automation, clipboard automation, and external web requests. Those paths predate the core permission boundary and should be migrated behind registered tools before unattended automation is enabled. Never expose the GUI to untrusted users without adding authentication and authorization.
