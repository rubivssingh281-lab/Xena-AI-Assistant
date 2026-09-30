# Testing

Run the core tests from the project directory:

```powershell
python -m unittest discover -s tests -v
```

The tests cover path traversal rejection, verified filesystem writes, SQLite task persistence, and approval gating. The legacy Tkinter, audio, webcam, DeepFace, browser, and Ollama integrations require their runtime dependencies and hardware/services for integration testing; they are not silently treated as tested by the core suite.
