"""Extensible, permission-aware tool registry with verified filesystem tools."""

from __future__ import annotations

import hashlib
import json
from dataclasses import dataclass
from pathlib import Path
from typing import Any, Callable

from .models import RiskLevel
from .security import require_nonempty_text, safe_path
from . import system_tools


@dataclass(frozen=True, slots=True)
class ToolDefinition:
    name: str
    description: str
    risk_level: RiskLevel
    handler: Callable[[dict[str, Any]], dict[str, Any]]


class ToolRegistry:
    def __init__(self):
        self._tools: dict[str, ToolDefinition] = {}

    def register(self, definition: ToolDefinition) -> None:
        if definition.name in self._tools:
            raise ValueError(f"Tool already registered: {definition.name}")
        self._tools[definition.name] = definition

    def get(self, name: str) -> ToolDefinition:
        try:
            return self._tools[name]
        except KeyError as exc:
            raise KeyError(f"Unknown tool: {name}") from exc

    def capabilities(self) -> list[dict[str, str]]:
        return [
            {"name": tool.name, "description": tool.description, "risk_level": tool.risk_level.value}
            for tool in self._tools.values()
        ]

    def execute(self, name: str, arguments: dict[str, Any]) -> dict[str, Any]:
        return self.get(name).handler(arguments)


def build_default_registry(root: str | Path) -> ToolRegistry:
    root_path = Path(root).expanduser().resolve()
    root_path.mkdir(parents=True, exist_ok=True)
    registry = ToolRegistry()

    def list_files(args: dict[str, Any]) -> dict[str, Any]:
        directory = safe_path(args.get("path", "."), root_path)
        if not directory.is_dir():
            raise ValueError("Requested path is not a directory")
        items = sorted(
            [{"name": item.name, "type": "directory" if item.is_dir() else "file"}
             for item in directory.iterdir()],
            key=lambda item: (item["type"], item["name"].lower()),
        )
        return {"path": str(directory), "items": items, "verified": True}

    def read_text(args: dict[str, Any]) -> dict[str, Any]:
        file_path = safe_path(require_nonempty_text(args.get("path"), "path"), root_path)
        if not file_path.is_file():
            raise ValueError("Requested path is not a file")
        content = file_path.read_text(encoding="utf-8")
        return {"path": str(file_path), "content": content, "sha256": hashlib.sha256(content.encode()).hexdigest(), "verified": True}

    def write_text(args: dict[str, Any]) -> dict[str, Any]:
        file_path = safe_path(require_nonempty_text(args.get("path"), "path"), root_path)
        content = args.get("content")
        if not isinstance(content, str):
            raise ValueError("content must be text")
        file_path.parent.mkdir(parents=True, exist_ok=True)
        file_path.write_text(content, encoding="utf-8", newline="")
        verified = file_path.is_file() and file_path.read_text(encoding="utf-8") == content
        if not verified:
            raise IOError("File write could not be verified")
        return {"path": str(file_path), "bytes": len(content.encode()), "verified": True}

    registry.register(ToolDefinition("filesystem.list", "List files under the configured automation root", RiskLevel.LOW, list_files))
    registry.register(ToolDefinition("filesystem.read_text", "Read a UTF-8 text file under the configured automation root", RiskLevel.LOW, read_text))
    registry.register(ToolDefinition("filesystem.write_text", "Create or replace a UTF-8 text file under the configured automation root", RiskLevel.MEDIUM, write_text))
    
    registry.register(ToolDefinition("system.open_terminal", "Open a terminal emulator", RiskLevel.LOW, system_tools.open_terminal))
    registry.register(ToolDefinition("system.open_browser", "Open a web browser (e.g., chrome, brave)", RiskLevel.LOW, system_tools.open_browser))
    registry.register(ToolDefinition("system.power_action", "Perform system power action (lock, sleep)", RiskLevel.MEDIUM, system_tools.power_action))
    registry.register(ToolDefinition("system.volume_control", "Control system volume (mute, up, down)", RiskLevel.LOW, system_tools.volume_control))
    
    return registry
