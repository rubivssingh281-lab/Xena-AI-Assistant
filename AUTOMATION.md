# Automation

The current automation slice supports explicit task plans, ordered workflow steps, local recurring interval callbacks, SQLite task history, approval gating, bounded retries, and verification-first filesystem actions.

The GUI exposes:

- `Automation Capabilities`
- `List Safe Files`
- voice commands for automation capabilities, safe file listing, and automation history

The automation root is `~/XenaAutomation`. Files outside that root are rejected. The scheduler is in-process and should be replaced or backed by a service for automations that must survive application restarts.
