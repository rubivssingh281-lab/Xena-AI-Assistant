# Deployment

Xena is currently a local desktop application, not a network service. Run it on a trusted Windows account with the optional dependencies from `requirements.txt` installed in a virtual environment.

Before deployment:

1. Confirm microphone, camera, and audio drivers if those features are needed.
2. Confirm Ollama is local and the configured model is installed.
3. Run the core unit tests and Python compilation check.
4. Keep `~/.xena_tasks.sqlite3` and `~/XenaAutomation` protected as user data.
5. Do not run the application as an administrator unless a specific feature requires it.
