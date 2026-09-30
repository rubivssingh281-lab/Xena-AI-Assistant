"""
xena_code.py - the edit engine behind Xena's Code Agent.

The model answers with a short message, then (only if code must change) a line
"### EDITS" followed by SEARCH/REPLACE blocks:

    path/to/file.py
    <<<<<<< SEARCH
    lines copied from the current file
    =======
    the lines that replace them
    >>>>>>> REPLACE

This module parses those blocks, applies them to the file text robustly (models
often get indentation or whitespace slightly wrong), and turns every change into
reviewable hunks that the editor can accept or reject one by one.
"""
from __future__ import annotations

import difflib
import re
import textwrap

EDIT_MARKER = "### EDITS"

_SEARCH = re.compile(r"^\s*<{5,9}\s*SEARCH\b")
_DIVIDER = re.compile(r"^\s*={5,9}\s*$")
_REPLACE = re.compile(r"^\s*>{5,9}\s*REPLACE\b")
_PATHISH = re.compile(r"^[\w\-.\\/ ]{1,200}\.[A-Za-z0-9]{1,12}$")
_FENCE = re.compile(r"^```[^\n]*\n(.*?)^```", re.M | re.S)


# ─────────────────────────────── parsing the reply ───────────────────────────────

def clean_path(line: str) -> str:
    p = line.strip()
    p = re.sub(r"^(?:#+\s*)?(?:file|path|filename)\s*[:=]\s*", "", p, flags=re.I)
    return p.strip("`*'\" :").replace("\\", "/")


def looks_like_path(p: str) -> bool:
    return bool(p) and bool(_PATHISH.match(p)) and p.count(" ") <= 2 and not p.startswith(("-", "#"))


def _tidy(message: str) -> str:
    """Trim a message and undo indentation the model put on every line."""
    lines = message.strip().split("\n")
    return (lines[0] + "\n" + textwrap.dedent("\n".join(lines[1:]))).rstrip() if len(lines) > 1 else lines[0]


def split_reply(text: str) -> tuple[str, str]:
    """(message for the operator, edits section)."""
    t = (text or "").replace("\r\n", "\n")
    idx = t.find(EDIT_MARKER)
    if idx >= 0:
        return _tidy(t[:idx]), t[idx + len(EDIT_MARKER):]
    m = re.search(r"^\s*<{5,9}\s*SEARCH\b", t, re.M)
    if m:   # blocks without the marker: the line before the first block is its path
        head = t[:m.start()].rstrip("\n").split("\n")
        if head and looks_like_path(clean_path(head[-1])):
            head = head[:-1]
        head_text = "\n".join(head)
        return _tidy(head_text), t[len(head_text):]
    return _tidy(t), ""


def live_message(partial: str) -> str:
    """The part of a reply that's still being streamed that should be shown."""
    message, _ = split_reply(partial)
    lines = message.split("\n")
    # Hide a half-written "### EDITS" / marker line at the very end, and a bare
    # file path just above it (that's the header of an edit block being written).
    if lines and lines[-1].lstrip().startswith(("#", "<", "=", ">")):
        lines = lines[:-1]
    while len(lines) > 1 and looks_like_path(clean_path(lines[-1])):
        lines = lines[:-1]
    return "\n".join(lines).rstrip()


def parse_blocks(text: str, default_path: str) -> list[dict]:
    """All SEARCH/REPLACE blocks in `text` as {"path", "search", "replace"}."""
    lines = (text or "").replace("\r\n", "\n").split("\n")
    blocks, last_path, i = [], default_path, 0
    while i < len(lines):
        if not _SEARCH.match(lines[i]):
            i += 1
            continue
        path, j = last_path, i - 1
        while j >= 0 and not lines[j].strip():
            j -= 1
        if j >= 0 and looks_like_path(clean_path(lines[j])):
            path = clean_path(lines[j])
        search, k = [], i + 1
        while k < len(lines) and not _DIVIDER.match(lines[k]):
            search.append(lines[k])
            k += 1
        replace, k = [], k + 1
        while k < len(lines) and not _REPLACE.match(lines[k]):
            replace.append(lines[k])
            k += 1
        if k >= len(lines):
            # The model never wrote ">>>>>>> REPLACE"; drop a stray closing fence it ended with.
            while replace and not replace[-1].strip():
                replace.pop()
            fences = sum(1 for l in replace if l.strip().startswith("```"))
            if replace and replace[-1].strip() == "```" and fences % 2 == 1:
                replace.pop()
        # Small models sometimes indent their whole reply; undo that when every line has it.
        indent = len(lines[i]) - len(lines[i].lstrip(" "))
        body = [l for l in search + replace if l.strip()]
        if indent and body and all(len(l) - len(l.lstrip(" ")) >= indent for l in body):
            search = [l[indent:] if l.strip() else "" for l in search]
            replace = [l[indent:] if l.strip() else "" for l in replace]
        blocks.append({"path": path, "search": "\n".join(search), "replace": "\n".join(replace)})
        last_path, i = path, k + 1
    return blocks


_PLACEHOLDER = re.compile(r"^\s*[(\[<{]?\s*(?:empty|none|nothing|new file|no existing content[^\n]*)\s*[)\]>}]?\s*$", re.I)


def new_file_content(block: dict) -> str:
    """Content for a block that creates a file. Models sometimes write a placeholder
    in SEARCH, or put the whole new file in SEARCH and leave REPLACE empty."""
    search, replace = block["search"], block["replace"]
    if not replace.strip() and search.strip() and not _PLACEHOLDER.match(search):
        return search
    return replace


def fenced_blocks(text: str) -> list[str]:
    return [m.group(1).rstrip("\n") for m in _FENCE.finditer((text or "").replace("\r\n", "\n"))]


# ─────────────────────────────── applying edits ───────────────────────────────

def _indent(line: str) -> str:
    return line[: len(line) - len(line.lstrip())]


def _reindent(lines: list[str], found_first: str, search_first: str) -> list[str]:
    """Shift replacement lines by the indentation difference between what the
    model wrote in SEARCH and what is really in the file."""
    have, wrote = _indent(found_first), _indent(search_first)
    if have == wrote:
        return lines
    out = []
    for line in lines:
        if not line.strip():
            out.append(line)
        elif line.startswith(wrote):
            out.append(have + line[len(wrote):])
        else:
            out.append(have + line.lstrip())
    return out


def apply_block(content: str, search: str, replace: str) -> tuple[str | None, str]:
    """Apply one SEARCH/REPLACE to `content`. Returns (new content | None, how)."""
    if search in content and search.strip():
        return content.replace(search, replace, 1), "exact"

    c_lines = content.split("\n")
    s_lines = search.split("\n")
    r_lines = replace.split("\n")
    while s_lines and not s_lines[0].strip():
        s_lines.pop(0)
    while s_lines and not s_lines[-1].strip():
        s_lines.pop()
    if not s_lines:
        return None, "empty search"
    n = len(s_lines)

    # Same lines, different trailing / leading whitespace.
    for how, norm in (("trailing whitespace", str.rstrip), ("indentation", str.strip)):
        target = [norm(l) for l in s_lines]
        for i in range(len(c_lines) - n + 1):
            if [norm(l) for l in c_lines[i:i + n]] == target:
                new = _reindent(r_lines, c_lines[i], s_lines[0]) if how == "indentation" else r_lines
                return "\n".join(c_lines[:i] + new + c_lines[i + n:]), how

    # Nearly the same lines (the model misremembered a detail).
    target = "\n".join(l.strip() for l in s_lines)
    best, best_i = 0.0, -1
    for i in range(len(c_lines) - n + 1):
        window = "\n".join(l.strip() for l in c_lines[i:i + n])
        sm = difflib.SequenceMatcher(None, window, target, autojunk=False)
        if sm.real_quick_ratio() < best or sm.quick_ratio() < best:
            continue
        r = sm.ratio()
        if r > best:
            best, best_i = r, i
    if best >= 0.88:
        new = _reindent(r_lines, c_lines[best_i], s_lines[0])
        return "\n".join(c_lines[:best_i] + new + c_lines[best_i + n:]), f"fuzzy {best:.2f}"
    return None, "not found"


def _resolve(path: str, files: dict[str, str]) -> str | None:
    """Match a path the model wrote to one of the files it was given."""
    p = path.replace("\\", "/").lower()
    p = p[2:] if p.startswith("./") else p
    for real in files:
        if real.replace("\\", "/").lower() == p:
            return real
    base = p.rsplit("/", 1)[-1]
    matches = [real for real in files if real.replace("\\", "/").lower().rsplit("/", 1)[-1] == base]
    return matches[0] if len(matches) == 1 else None


def apply_edits(files: dict[str, str], blocks: list[dict], default_path: str):
    """Apply every block. Returns (changed {path: text}, new_files {path: text}, failed [..], notes [..])."""
    proposed = dict(files)
    new_files: dict[str, str] = {}
    failed, notes = [], []
    for b in blocks:
        target = _resolve(b["path"], proposed)
        if target is None and (not b["search"].strip() or _PLACEHOLDER.match(b["search"])):
            # An empty (or placeholder) SEARCH on an unknown path: a brand new file.
            path = b["path"].replace("\\", "/")
            new_files[path] = (new_files.get(path, "") + ("\n" if path in new_files else "") + new_file_content(b))
            continue
        candidates = [target] if target else []
        if default_path in proposed and default_path not in candidates:
            candidates.append(default_path)   # models often get the file name wrong
        for path in candidates:
            if not b["search"].strip():
                # Empty SEARCH on an existing file: append.
                text = proposed[path]
                proposed[path] = text + ("" if text.endswith("\n") or not text else "\n") + b["replace"]
                break
            result, how = apply_block(proposed[path], b["search"], b["replace"])
            if result is not None:
                proposed[path] = result
                if how != "exact":
                    notes.append(f"{path}: matched by {how}")
                break
        else:
            if target is None and looks_like_path(b["path"]):
                # A path we weren't given, and its SEARCH isn't in the open file: a new
                # file (small models often put a placeholder in SEARCH instead of nothing).
                path = b["path"].replace("\\", "/")
                new_files[path] = (new_files.get(path, "") + ("\n" if path in new_files else "") + new_file_content(b))
            else:
                failed.append({"path": b["path"], "search": "\n".join(b["search"].split("\n")[:4])})
    changed = {p: t for p, t in proposed.items() if t != files.get(p)}
    return changed, new_files, failed, notes


# ─────────────────────────────── reviewable hunks ───────────────────────────────

def hunks(old: str, new: str, context: int = 3) -> list[dict]:
    """Changes between two texts as hunks: old_start is the 0-based line in `old`;
    old/new are the lines replaced; before/after are context lines (for display
    and for re-finding the spot if the file was edited in the meantime)."""
    a, b = old.split("\n"), new.split("\n")
    groups = []
    for tag, i1, i2, j1, j2 in difflib.SequenceMatcher(None, a, b, autojunk=False).get_opcodes():
        if tag == "equal":
            continue
        if groups and i1 - groups[-1][1] <= 2:   # merge changes that are close together
            groups[-1][1], groups[-1][3] = i2, j2
        else:
            groups.append([i1, i2, j1, j2])
    return [{"id": k, "old_start": i1, "old": a[i1:i2], "new": b[j1:j2],
             "before": a[max(0, i1 - context):i1], "after": a[i2:i2 + context]}
            for k, (i1, i2, j1, j2) in enumerate(groups)]
