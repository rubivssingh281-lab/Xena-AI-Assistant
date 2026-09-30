"""Small SQLite persistence layer for tasks, events, approvals, and artifacts."""

from __future__ import annotations

import json
import sqlite3
import threading
from pathlib import Path
from typing import Any


class TaskStore:
    def __init__(self, database_path: str | Path):
        self.database_path = str(database_path)
        self._lock = threading.RLock()
        self._connection = sqlite3.connect(self.database_path, check_same_thread=False)
        self._connection.row_factory = sqlite3.Row
        self._initialize()

    def _initialize(self) -> None:
        with self._lock, self._connection:
            self._connection.executescript(
                """
                CREATE TABLE IF NOT EXISTS tasks (
                    task_id TEXT PRIMARY KEY,
                    goal TEXT NOT NULL,
                    status TEXT NOT NULL,
                    payload TEXT NOT NULL,
                    created_at TEXT NOT NULL,
                    updated_at TEXT NOT NULL
                );
                CREATE TABLE IF NOT EXISTS task_events (
                    event_id INTEGER PRIMARY KEY AUTOINCREMENT,
                    task_id TEXT NOT NULL,
                    event TEXT NOT NULL,
                    payload TEXT NOT NULL,
                    created_at TEXT NOT NULL
                );
                CREATE TABLE IF NOT EXISTS approvals (
                    approval_id TEXT PRIMARY KEY,
                    task_id TEXT NOT NULL,
                    step_id TEXT NOT NULL,
                    status TEXT NOT NULL,
                    reason TEXT NOT NULL,
                    created_at TEXT NOT NULL
                );
                CREATE TABLE IF NOT EXISTS artifacts (
                    artifact_id TEXT PRIMARY KEY,
                    task_id TEXT NOT NULL,
                    kind TEXT NOT NULL,
                    location TEXT NOT NULL,
                    checksum TEXT,
                    metadata TEXT NOT NULL,
                    created_at TEXT NOT NULL
                );
                """
            )

    def save_task(self, task: dict[str, Any], updated_at: str) -> None:
        with self._lock, self._connection:
            self._connection.execute(
                """INSERT INTO tasks(task_id, goal, status, payload, created_at, updated_at)
                   VALUES (?, ?, ?, ?, ?, ?)
                   ON CONFLICT(task_id) DO UPDATE SET
                     goal=excluded.goal, status=excluded.status,
                     payload=excluded.payload, updated_at=excluded.updated_at""",
                (task["task_id"], task["goal"], task["status"], json.dumps(task), task["created_at"], updated_at),
            )

    def add_event(self, task_id: str, event: str, payload: dict[str, Any], created_at: str) -> None:
        with self._lock, self._connection:
            self._connection.execute(
                "INSERT INTO task_events(task_id, event, payload, created_at) VALUES (?, ?, ?, ?)",
                (task_id, event, json.dumps(payload), created_at),
            )

    def add_approval(self, approval_id: str, task_id: str, step_id: str, reason: str, created_at: str) -> None:
        with self._lock, self._connection:
            self._connection.execute(
                "INSERT INTO approvals(approval_id, task_id, step_id, status, reason, created_at) VALUES (?, ?, ?, ?, ?, ?)",
                (approval_id, task_id, step_id, "pending", reason, created_at),
            )

    def get_task(self, task_id: str) -> dict[str, Any] | None:
        with self._lock:
            row = self._connection.execute("SELECT payload FROM tasks WHERE task_id = ?", (task_id,)).fetchone()
        return json.loads(row["payload"]) if row else None

    def list_tasks(self, limit: int = 50) -> list[dict[str, Any]]:
        limit = max(1, min(limit, 500))
        with self._lock:
            rows = self._connection.execute(
                "SELECT payload FROM tasks ORDER BY updated_at DESC LIMIT ?", (limit,)
            ).fetchall()
        return [json.loads(row["payload"]) for row in rows]

    def close(self) -> None:
        with self._lock:
            self._connection.close()
