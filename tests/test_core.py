import tempfile
import unittest
from pathlib import Path

from xena_core.engine import AutomationEngine
from xena_core.models import RiskLevel, TaskPlan, ToolCall, WorkflowStep, TaskStatus
from xena_core.security import SecurityError, safe_path
from xena_core.scheduler import AutomationScheduler
from xena_core.storage import TaskStore
from xena_core.tools import build_default_registry


class CoreTests(unittest.TestCase):
    def test_path_traversal_is_rejected(self):
        with tempfile.TemporaryDirectory() as directory:
            with self.assertRaises(SecurityError):
                safe_path("../outside.txt", Path(directory))

    def test_write_then_read_is_verified_and_persisted(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory) / "root"
            registry = build_default_registry(root)
            store = TaskStore(Path(directory) / "tasks.sqlite3")
            engine = AutomationEngine(registry, store)
            plan = TaskPlan(
                goal="create a note",
                steps=[WorkflowStep("write", ToolCall("filesystem.write_text", {"path": "note.txt", "content": "hello"}), RiskLevel.MEDIUM)],
            )
            result = engine.execute(plan)
            self.assertEqual(result.status, TaskStatus.COMPLETED)
            self.assertEqual((root / "note.txt").read_text(encoding="utf-8"), "hello")
            self.assertEqual(store.get_task(plan.task_id)["status"], TaskStatus.COMPLETED)
            store.close()

    def test_medium_risk_can_require_approval(self):
        with tempfile.TemporaryDirectory() as directory:
            registry = build_default_registry(Path(directory) / "root")
            store = TaskStore(Path(directory) / "tasks.sqlite3")
            engine = AutomationEngine(registry, store, approval_policy=lambda risk: risk is not RiskLevel.LOW)
            plan = TaskPlan(goal="write", steps=[WorkflowStep("write", ToolCall("filesystem.write_text", {"path": "x", "content": "y"}), RiskLevel.MEDIUM)])
            result = engine.execute(plan)
            self.assertEqual(result.status, TaskStatus.WAITING_APPROVAL)
            self.assertFalse((Path(directory) / "root" / "x").exists())
            store.close()

    def test_scheduler_assigns_unique_ids(self):
        scheduler = AutomationScheduler()
        first = scheduler.add_interval(lambda: None, 60)
        second = scheduler.add_interval(lambda: None, 60)
        self.assertNotEqual(first, second)
        self.assertTrue(scheduler.cancel(first))
        scheduler.shutdown()


if __name__ == "__main__":
    unittest.main()
