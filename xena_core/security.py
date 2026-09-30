"""Security boundaries for local automation tools."""

from __future__ import annotations

import os
from pathlib import Path


class SecurityError(ValueError):
    """Raised when an automation request crosses a configured boundary."""


def safe_path(path: str | os.PathLike[str], root: Path) -> Path:
    """Resolve a path and reject traversal outside the configured root."""
    root = root.expanduser().resolve()
    candidate = Path(path).expanduser()
    if not candidate.is_absolute():
        candidate = root / candidate
    candidate = candidate.resolve()
    try:
        candidate.relative_to(root)
    except ValueError as exc:
        raise SecurityError(f"Path is outside the automation root: {path}") from exc
    return candidate


def require_nonempty_text(value: object, field_name: str) -> str:
    if not isinstance(value, str) or not value.strip():
        raise SecurityError(f"{field_name} must be non-empty text")
    return value.strip()
