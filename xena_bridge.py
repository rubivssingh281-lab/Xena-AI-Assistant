import json
import queue
import sys
import threading
import tkinter as tk
from pathlib import Path

from Xena import AdvancedVoiceAssistant

import socket

requests = queue.Queue()
response_queue = queue.Queue()

# Actions that talk to the language model run one at a time in their own lane;
# everything else (volume, camera, screenshots, ...) runs in a second lane so a
# long reply never blocks a button. Control messages are answered immediately.
LLM_ACTIONS = {"command", "code_agent", "code_generate"}
CONTROL_ACTIONS = {"ping", "cancel", "stop_speaking", "set_voice"}

cancelled: set = set()          # request ids the operator stopped
voice = {"on": True}            # spoken replies on/off (the text is always shown)


def read_requests(conn):
    try:
        f = conn.makefile('r', encoding='utf-8-sig')
        for line in f:
            if not line.strip(): continue
            try:
                requests.put(json.loads(line))
            except json.JSONDecodeError as exc:
                response_queue.put(json.dumps({"id": None, "error": f"Invalid request: {exc}"}))
    except Exception as e:
        print(f"Connection error: {e}")
    requests.put(None)   # the desktop app went away


def write_responses(conn):
    try:
        f = conn.makefile('w', encoding='utf-8')
        while True:
            msg = response_queue.get()
            if msg is None: break
            f.write(msg + '\n')
            f.flush()
    except Exception as e:
        pass


def invoke(app, action, args, emit=None):
    """Run one action. `emit(value)` streams a partial update (a text chunk or
    a {"notes", "code"} snapshot) to the desktop app before the final result."""
    automation_actions = {
        "open_terminal": ("system.open_terminal", {}),
        "open_chrome": ("system.open_browser", {"browser": "chrome"}),
        "open_brave": ("system.open_browser", {"browser": "brave"}),
        "lock_screen": ("system.power_action", {"action": "lock"}),
        "sleep_system": ("system.power_action", {"action": "sleep"}),
        "volume_mute": ("system.volume_control", {"action": "mute"}),
        "volume_up": ("system.volume_control", {"action": "up"}),
        "volume_down": ("system.volume_control", {"action": "down"}),
    }

    if action in automation_actions:
        tool_name, tool_args = automation_actions[action]
        from xena_core import TaskPlan, WorkflowStep, ToolCall, RiskLevel
        plan = TaskPlan(
            goal=f"Execute {action}",
            steps=[WorkflowStep(
                name=f"Run {tool_name}",
                tool_call=ToolCall(tool_name, tool_args),
                risk_level=RiskLevel.LOW if tool_name != "system.power_action" else RiskLevel.MEDIUM,
                verify=False
            )]
        )
        result = app.automation_engine.execute(plan, approved=True)
        if result.status.value == "completed":
            return f"{action.replace('_', ' ').title()} executed successfully via core registry."
        else:
            return f"Failed to execute {action}: {result.failures}"

    actions = {
        "command": lambda: app.process_command(args.get("text", ""), on_token=emit)
                           or "Command dispatched.",
        "code_agent": lambda: app.code_agent(args.get("request", "{}"), on_token=emit),
        "code_generate": lambda: app.code_generate(
            args.get("instruction", ""), args.get("filename", ""), args.get("content", ""),
            on_token=emit,
        ),
        "toggle_listening": app.toggle_listening,
        "toggle_camera": app.toggle_camera,
        "analyze_face": app.capture_analyze,
        "recognize_face": app.recognize_now,
        "rebuild_faces": app.rebuild_face_gallery,
        "camera_port": app.camera_port,
        "open_whatsapp": app.open_whatsapp,
        "take_screenshot": app.take_screenshot,
        "create_folder": lambda: app.create_folder(args.get("name", "XenaFolder")),
        "create_file": lambda: app.create_file(args.get("name", "xena_note.txt")),
        "calculate": lambda: app.calculate(args.get("expression", "15 plus 7")),
        "brightness_up": app.brightness_up,
        "brightness_down": app.brightness_down,
        "list_safe_files": app.list_automation_files,
        "automation_capabilities": app.show_automation_capabilities,
        "system_info": app.show_system_info,
        "open_calculator": app.open_calculator,
        "open_task_manager": app.open_task_manager,
        "open_file_explorer": app.open_file_explorer,
    }
    handler = actions.get(action)
    if handler is None:
        raise ValueError(f"Unknown action: {action}")
    result = handler()
    return result if isinstance(result, str) else "Action completed."


def stop_speaking(app):
    """Silence Xena now: drop queued sentences and stop the one playing."""
    try:
        while True:
            app.speech_queue.get_nowait()
    except (queue.Empty, AttributeError):
        pass
    if getattr(app, "_mixer_ready", False):
        try:
            import pygame
            pygame.mixer.music.stop()
        except Exception:
            pass


def control(app, action, args):
    """Instant actions, answered straight from the dispatcher (never queued)."""
    if action == "ping":
        return "pong"
    if action == "cancel":
        try:
            cancelled.add(int(args.get("id", "")))
        except ValueError:
            pass
        stop_speaking(app)
        return "ok"
    if action == "stop_speaking":
        stop_speaking(app)
        return "ok"
    if action == "set_voice":
        voice["on"] = str(args.get("on", "true")).lower() != "false"
        if not voice["on"]:
            stop_speaking(app)
        return "on" if voice["on"] else "off"
    raise ValueError(f"Unknown action: {action}")


def worker(app, lane: queue.Queue):
    while True:
        request = lane.get()
        request_id = request.get("id")
        action = request.get("action", "")
        if request_id in cancelled:   # stopped while it was still waiting its turn
            cancelled.discard(request_id)
            response_queue.put(json.dumps({"id": request_id, "cancelled": True}))
            continue

        def emit(value, _rid=request_id):
            if _rid in cancelled:
                return False          # tells the model stream to stop
            response_queue.put(json.dumps({"id": _rid, "partial": value}))
            return None

        try:
            result = invoke(app, action, request.get("args", {}), emit)
            response_queue.put(json.dumps({"id": request_id, "result": result}))
        except Exception as exc:
            response_queue.put(json.dumps({"id": request_id, "error": str(exc)}))
        finally:
            cancelled.discard(request_id)


def main():
    server = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
    server.bind(('127.0.0.1', 0))
    server.listen(1)
    port = server.getsockname()[1]

    # Announce port to standard output so C# process can connect
    print(f"PORT:{port}", flush=True)

    app = AdvancedVoiceAssistant()

    # Spoken replies can be switched off from the desktop app.
    real_speak = app.speak
    app.speak = lambda text: real_speak(text) if voice["on"] else None

    # Wait for C# desktop app to connect
    conn, addr = server.accept()

    threading.Thread(target=read_requests, args=(conn,), daemon=True).start()
    threading.Thread(target=write_responses, args=(conn,), daemon=True).start()

    lanes = {"llm": queue.Queue(), "quick": queue.Queue()}
    for lane in lanes.values():
        threading.Thread(target=worker, args=(app, lane), daemon=True).start()

    while True:
        request = requests.get()
        if request is None:
            break
        request_id = request.get("id")
        action = request.get("action", "")
        if action == "shutdown_bridge":
            app.shutdown()
            response_queue.put(json.dumps({"id": request_id, "result": "Bridge stopped."}))
            break
        if action in CONTROL_ACTIONS:
            try:
                result = control(app, action, request.get("args", {}))
                response_queue.put(json.dumps({"id": request_id, "result": result}))
            except Exception as exc:
                response_queue.put(json.dumps({"id": request_id, "error": str(exc)}))
            continue
        lanes["llm" if action in LLM_ACTIONS else "quick"].put(request)


if __name__ == "__main__":
    main()
