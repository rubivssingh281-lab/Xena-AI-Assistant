"""Minimal persisted-friendly scheduler for local recurring automations."""

from __future__ import annotations

import threading
import time
from dataclasses import dataclass, field
from datetime import datetime, timezone
from typing import Callable
from uuid import uuid4


@dataclass(slots=True)
class ScheduledAutomation:
    callback: Callable[[], None]
    interval_seconds: float
    automation_id: str = field(default_factory=lambda: uuid4().hex)
    enabled: bool = True


class AutomationScheduler:
    """Run callbacks at bounded intervals in a daemon thread.

    Scheduling is deliberately in-process and local. A future service can persist
    these definitions without changing the callback contract.
    """

    def __init__(self):
        self._items: dict[str, ScheduledAutomation] = {}
        self._lock = threading.RLock()
        self._stop = threading.Event()
        self._thread = threading.Thread(target=self._run, daemon=True, name="xena-scheduler")
        self._thread.start()

    def add_interval(self, callback: Callable[[], None], interval_seconds: float) -> str:
        if interval_seconds <= 0:
            raise ValueError("interval_seconds must be positive")
        item = ScheduledAutomation(callback=callback, interval_seconds=interval_seconds)
        with self._lock:
            self._items[item.automation_id] = item
        return item.automation_id

    def cancel(self, automation_id: str) -> bool:
        with self._lock:
            item = self._items.get(automation_id)
            if not item:
                return False
            item.enabled = False
            del self._items[automation_id]
            return True

    def list_ids(self) -> list[str]:
        with self._lock:
            return list(self._items)

    def shutdown(self) -> None:
        self._stop.set()
        self._thread.join(timeout=2)

    def _run(self) -> None:
        next_run: dict[str, float] = {}
        while not self._stop.wait(0.25):
            now = time.monotonic()
            with self._lock:
                items = list(self._items.items())
            for automation_id, item in items:
                due = next_run.setdefault(automation_id, now + item.interval_seconds)
                if item.enabled and now >= due:
                    try:
                        item.callback()
                    except Exception:
                        pass
                    next_run[automation_id] = now + item.interval_seconds
