# Xena — Advanced Voice Assistant

>Xena is a Windows-oriented desktop voice assistant with speech, TTS, GUI automation, webcam/DeepFace recognition, browser controls, and local Ollama chat. A standard-library automation core is now available under `xena_core` without replacing the existing desktop behavior. A feature-rich, AI-powered desktop voice assistant built with Python. Xena combines voice recognition, text-to-speech, a local LLM (via Ollama), real-time face/emotion recognition, system control, and much more — all wrapped in a sleek dark-themed C# GUI.


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

The core creates `~/.xena_tasks.sqlite3` and restricts its filesystem tools to `~/XenaAutomation`. See [ARCHITECTURE.md](ARCHITECTURE.md), [AUTOMATION.md](AUTOMATION.md), [TOOLS.md](TOOLS.md), [SECURITY.md](SECURITY.md), and [TESTING.md](TESTING.md).

