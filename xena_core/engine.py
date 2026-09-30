"""Bounded workflow execution with approvals, retries, and verification."""

from __future__ import annotations

from datetime import datetime, timezone
from typing import Callable
from uuid import uuid4

from .models import RiskLevel, TaskPlan, TaskStatus
from .storage import TaskStore
from .tools import ToolRegistry


class AutomationEngine:
    def __init__(
        self,
        registry: ToolRegistry,
        store: TaskStore,
        approval_policy: Callable[[RiskLevel], bool] | None = None,
        max_steps: int = 20,
        max_retries: int = 2,
    ):
        self.registry = registry
        self.store = store
        self.approval_policy = approval_policy or (lambda risk: risk is RiskLevel.HIGH)
        self.max_steps = max(1, max_steps)
        self.max_retries = max(0, max_retries)

    def execute(self, plan: TaskPlan, approved: bool = False) -> TaskPlan:
        self._save(plan, "task_started")
        plan.status = TaskStatus.RUNNING
        self._save(plan, "task_running")
        if len(plan.steps) > self.max_steps:
            return self._fail(plan, "plan exceeds maximum step count")

        for index, step in enumerate(plan.steps):
            plan.current_step = index
            if self.approval_policy(step.risk_level) and not approved:
                plan.status = TaskStatus.WAITING_APPROVAL
                approval_id = uuid4().hex
                self.store.add_approval(
                    approval_id, plan.task_id, step.step_id,
                    f"Approval required for {step.risk_level.value}-risk tool: {step.tool_call.tool_name}",
                    self._now(),
                )
                self._save(plan, "approval_required", {"step_id": step.step_id, "approval_id": approval_id})
                return plan

            try:
                result = self._execute_with_retry(step.tool_call.tool_name, step.tool_call.arguments)
                if step.verify and not result.get("verified", False):
                    raise RuntimeError(f"Tool {step.tool_call.tool_name} returned an unverified result")
                plan.artifacts.append({"step_id": step.step_id, "tool": step.tool_call.tool_name, "result": result})
                self._save(plan, "step_completed", {"step_id": step.step_id})
            except Exception as exc:
                return self._fail(plan, f"step '{step.name}' failed: {exc}", step.step_id)

        plan.status = TaskStatus.COMPLETED
        self._save(plan, "task_completed")
        return plan

    def _execute_with_retry(self, tool_name: str, arguments: dict) -> dict:
        last_error: Exception | None = None
        for _ in range(self.max_retries + 1):
            try:
                return self.registry.execute(tool_name, arguments)
            except (OSError, TimeoutError, ConnectionError) as exc:
                last_error = exc
        if last_error:
            raise last_error
        return self.registry.execute(tool_name, arguments)

    def _fail(self, plan: TaskPlan, message: str, step_id: str | None = None) -> TaskPlan:
        plan.status = TaskStatus.FAILED
        failure = {"message": message}
        if step_id:
            failure["step_id"] = step_id
        plan.failures.append(failure)
        self._save(plan, "task_failed", failure)
        return plan

    def _save(self, plan: TaskPlan, event: str, payload: dict | None = None) -> None:
        self.store.save_task(plan.to_dict(), self._now())
        self.store.add_event(plan.task_id, event, payload or {}, self._now())

    @staticmethod
    def _now() -> str:
        return datetime.now(timezone.utc).isoformat()
