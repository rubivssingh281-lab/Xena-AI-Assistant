# Xena — Advanced Voice Assistant

> Xena is an advanced, Windows-oriented desktop AI voice assistant designed to streamline your daily tasks through powerful automation and intelligent interactions. With support for speech recognition, text-to-speech (TTS), GUI automation, webcam integration with DeepFace recognition, browser controls, and local Ollama chat, Xena acts as a comprehensive, hands-free productivity companion.

A standard-library automation core is now available under `xena_core` to provide a robust execution engine without replacing the existing desktop behavior.

## Key Features

- **Voice & Speech Interactions**: Seamless hands-free operation with integrated speech recognition and natural text-to-speech (TTS) responses.
- **Local AI Chat**: Fully integrated with Ollama for secure, offline AI chatting and task processing with local large language models.
- **Facial Recognition**: Built-in webcam integration leveraging DeepFace to provide advanced facial recognition and computer vision capabilities.
- **Desktop & GUI Automation**: Automate repetitive tasks, manage the clipboard, and control desktop applications programmatically.
- **Browser Automation**: Programmatic web navigation and browser controls for gathering information or interacting with web pages.
- **Secure Execution Core**: A robust `xena_core` engine featuring an approval-gated tool registry, bounded task retries, SQLite-backed task history, and verified tool execution.
- **Modern Windows HUD**: A redesigned, native WPF-based desktop presentation layer (`Xena.Desktop`) for a sleek, responsive UI.
- **Coding Agent Mode**: A locally LLM designed for getting your code verified and Edited with a dedicated IDE with Change accept/reject feature.

## Setup

Use Python 3.12 or newer, create a virtual environment, and install the packages in `requirements.txt`:

```powershell
python -m venv .venv
.\.venv\Scripts\Activate.ps1
python -m pip install -r requirements.txt
python Xena.py
```

## C# desktop interface

The redesigned Windows HUD is available as a native WPF application under `Xena.Desktop`:

```powershell
dotnet run --project .\Xena.Desktop\Xena.Desktop.csproj
```

The C# project provides the new desktop presentation layer. The existing Python application remains the runtime for speech, camera, DeepFace, and automation integrations while the migration is completed.

The legacy GUI additionally requires a working microphone, audio driver, camera for face features, and Ollama at `http://localhost:11434` with the configured model. These are external prerequisites, not bundled services.

## Core checks

```powershell
python -m unittest discover -s tests -v
python -m py_compile Xena.py
```

The core creates `~/.xena_tasks.sqlite3` and restricts its filesystem tools to `~/XenaAutomation`.

## Architecture

Xena currently has a Tkinter desktop shell in `Xena.py`. The orchestration boundary is now separated into the standard-library-only `xena_core` package.

### Core flow

1. A caller builds a `TaskPlan` containing ordered `WorkflowStep` values.
2. `ToolRegistry` resolves each declared tool and exposes capability metadata.
3. `AutomationEngine` enforces a maximum step count, risk approval policy, bounded retries, and verification.
4. `TaskStore` persists task state, task events, approval requests, and artifact records in SQLite.
5. `AutomationScheduler` supports local recurring interval callbacks without blocking the GUI.

### Security boundaries

Filesystem tools are restricted to a configured root and reject traversal. No arbitrary shell tool is registered. Medium/high-risk tools can require approval before execution. Tool results must explicitly report `verified: true` when verification is enabled.

The existing GUI, speech, browser, system, and DeepFace integrations remain in `Xena.py`; they are not yet migrated into the registry because several of them perform external or destructive actions and need individual permission policies.

## Automation

The current automation slice supports explicit task plans, ordered workflow steps, local recurring interval callbacks, SQLite task history, approval gating, bounded retries, and verification-first filesystem actions.

The GUI exposes:

- `Automation Capabilities`
- `List Safe Files`
- voice commands for automation capabilities, safe file listing, and automation history

The automation root is `~/XenaAutomation`. Files outside that root are rejected. The scheduler is in-process and should be replaced or backed by a service for automations that must survive application restarts.

## Deployment

Xena is currently a local desktop application, not a network service. Run it on a trusted Windows account with the optional dependencies from `requirements.txt` installed in a virtual environment.

Before deployment:

1. Confirm microphone, camera, and audio drivers if those features are needed.
2. Confirm Ollama is local and the configured model is installed.
3. Run the core unit tests and Python compilation check.
4. Keep `~/.xena_tasks.sqlite3` and `~/XenaAutomation` protected as user data.
5. Do not run the application as an administrator unless a specific feature requires it.

## Security

The automation core applies these controls:

- Filesystem paths are resolved and checked against `~/XenaAutomation`.
- No arbitrary command execution is exposed through the core registry.
- Tool risk is explicit and approval policies are injectable.
- Plans have a maximum step count and retries are bounded.
- Tool results must be verified before a verified step is accepted.
- Task events and approval requests are persisted in SQLite.

The legacy assistant still contains powerful features such as terminal command launch, shutdown/restart, browser automation, clipboard automation, and external web requests. Those paths predate the core permission boundary and should be migrated behind registered tools before unattended automation is enabled. Never expose the GUI to untrusted users without adding authentication and authorization.

## Testing

Run the core tests from the project directory:

```powershell
python -m unittest discover -s tests -v
```

The tests cover path traversal rejection, verified filesystem writes, SQLite task persistence, and approval gating. The legacy Tkinter, audio, webcam, DeepFace, browser, and Ollama integrations require their runtime dependencies and hardware/services for integration testing; they are not silently treated as tested by the core suite.

## Tools

Tools are registered with `ToolRegistry` using a name, description, risk level, and handler. The default registry currently provides:

- `filesystem.list` (low risk)
- `filesystem.read_text` (low risk)
- `filesystem.write_text` (medium risk)

A new tool should provide validated arguments, a real handler, a declared risk level, and a result containing `verified: true` when the requested state has been checked. Shell execution, browser control, messaging, and destructive system operations are intentionally not registered in this core until they have dedicated permission and audit policies.

## License

MIT License

Copyright (c) 2026 Saksham Singh

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
