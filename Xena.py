import token
import sounddevice as sd
import tempfile
import os
import time
from email.mime import text
import tkinter as tk
from tkinter import ttk, scrolledtext, messagebox
import speech_recognition as sr
import pyttsx3
import threading
import time
import re
import webbrowser
import datetime
import requests
import json
import subprocess
import psutil
import pyautogui
import screen_brightness_control as sbcpi
from PIL import Image, ImageTk
import sys
import platform
import ctypes
import math
import shutil
import queue
import cv2
from deepface import DeepFace
import numpy as np

# ─────────────────────────────────────────────────────────────────────────────
# Helper – run a shell command in a background thread so the GUI never blocks
# ─────────────────────────────────────────────────────────────────────────────
def _run_bg(cmd_list_or_str, shell=False):
    """Fire-and-forget subprocess call."""
    try:
        if shell:
            subprocess.Popen(cmd_list_or_str, shell=True)
        else:
            subprocess.Popen(cmd_list_or_str)
    except Exception as e:
        print(f"[_run_bg] error: {e}")


# ─────────────────────────────────────────────────────────────────────────────
class AdvancedVoiceAssistant:
    def __init__(self, root):
        self.root = root
        self.root.title("Advanced Voice Assistant - Xena System")
        self.root.geometry("1920x1080")
        self.root.configure(bg="#000000")

        # ── Speech engine ──────────────────────────────────────────────────
        self.engine = pyttsx3.init()
        # Inside __init__, after initialising self.engine
        current_rate = self.engine.getProperty('rate')
        self.engine.setProperty('rate', current_rate - 27)
        voices = self.engine.getProperty('voices')
        if not voices:
            self.add_to_log("System", "WARNING: No TTS voices found! Speak will be silent.")
            print("ERROR: pyttsx3 has no voices.")
        elif len(voices) >= 2:
            self.engine.setProperty('voice', voices[1].id)
        else:
            self.engine.setProperty('voice', voices[0].id)   # use the only voice

        self._llm_token_line_started = False

        # ── Speech recognition ─────────────────────────────────────────────
        self.recognizer = sr.Recognizer()
        try:
            import sounddevice as sd
            sd.check_input_settings()
            self.mic_available = True
        except Exception:
            self.mic_available = False

        self.is_listening = False
        self.current_app  = None
        self.app_commands = {}
        self.system_status = {}

        # ---- Face / Emotion Recognition ----
        self.camera = None
        self.camera_running = False
        self.known_faces_dir = "known_faces"   # folder with reference images
        os.makedirs(self.known_faces_dir, exist_ok=True)
        self.face_recognition_enabled = False  # toggle for live recognition

        # ── Offline LLM ─────────────────────────────────────────────────
        self.llm_queue = queue.Queue()
        self.speech_queue = queue.Queue()
        self.speech_worker_running = True
        threading.Thread(target=self._speech_worker, daemon=True).start()
        self.engine.startLoop(False)
        self.setup_app_commands()
        self.create_gui()
        self.update_system_status()
        self.llm_busy = False 
        self.llm_buffer = []          # accumulates LLM tokens
        self.llm_current_speaker = None

    # =========================================================================
    # APP COMMAND MAP
    # =========================================================================

    def setup_app_commands(self):
        self.app_commands = {
            'chrome': {
                'new tab':      lambda: pyautogui.hotkey('ctrl', 't'),
                'close tab':    lambda: pyautogui.hotkey('ctrl', 'w'),
                'next tab':     lambda: pyautogui.hotkey('ctrl', 'tab'),
                'previous tab': lambda: pyautogui.hotkey('ctrl', 'shift', 'tab'),
                'refresh':      lambda: pyautogui.hotkey('ctrl', 'r'),
                'bookmarks':    lambda: pyautogui.hotkey('ctrl', 'shift', 'o'),
                'history':      lambda: pyautogui.hotkey('ctrl', 'h'),
                'downloads':    lambda: pyautogui.hotkey('ctrl', 'j'),
                'incognito':    lambda: pyautogui.hotkey('ctrl', 'shift', 'n'),
                'find':         lambda: pyautogui.hotkey('ctrl', 'f'),
            },
            'notepad': {
                'save':       lambda: pyautogui.hotkey('ctrl', 's'),
                'new file':   lambda: pyautogui.hotkey('ctrl', 'n'),
                'open':       lambda: pyautogui.hotkey('ctrl', 'o'),
                'select all': lambda: pyautogui.hotkey('ctrl', 'a'),
                'copy':       lambda: pyautogui.hotkey('ctrl', 'c'),
                'paste':      lambda: pyautogui.hotkey('ctrl', 'v'),
                'cut':        lambda: pyautogui.hotkey('ctrl', 'x'),
                'undo':       lambda: pyautogui.hotkey('ctrl', 'z'),
                'find':       lambda: pyautogui.hotkey('ctrl', 'f'),
                'replace':    lambda: pyautogui.hotkey('ctrl', 'h'),
            },
            'file explorer': {
                'new folder':       lambda: pyautogui.hotkey('ctrl', 'shift', 'n'),
                'rename':           lambda: pyautogui.press('f2'),
                'copy':             lambda: pyautogui.hotkey('ctrl', 'c'),
                'paste':            lambda: pyautogui.hotkey('ctrl', 'v'),
                'delete':           lambda: pyautogui.press('delete'),
                'select all':       lambda: pyautogui.hotkey('ctrl', 'a'),
                'properties':       lambda: pyautogui.hotkey('alt', 'enter'),
                'view large icons': lambda: pyautogui.hotkey('ctrl', 'shift', '2'),
                'view details':     lambda: pyautogui.hotkey('ctrl', 'shift', '6'),
            },
            'vlc': {
                'play':        lambda: pyautogui.press('space'),
                'pause':       lambda: pyautogui.press('space'),
                'stop':        lambda: pyautogui.press('s'),
                'next':        lambda: pyautogui.press('n'),
                'previous':    lambda: pyautogui.press('p'),
                'volume up':   lambda: pyautogui.hotkey('ctrl', 'up'),
                'volume down': lambda: pyautogui.hotkey('ctrl', 'down'),
                'fullscreen':  lambda: pyautogui.press('f'),
                'mute':        lambda: pyautogui.press('m'),
            },
            'system': {
                'lock screen': self.lock_screen,
                'shutdown':    self.shutdown_system,
                'restart':     self.restart_system,
                'sleep':       self.sleep_system,
                'task manager': self.open_task_manager,
                'system info': self.show_system_info,
            },
        }

    # =========================================================================
    # GUI
    # =========================================================================

    def create_gui(self):
        # ── Header ────────────────────────────────────────────────────────
        header_frame = tk.Frame(self.root, bg="#000000")
        header_frame.pack(fill=tk.X, padx=20, pady=10)

        tk.Label(
            header_frame,
            text="Xena - Advanced Voice Assistant",
            font=('Arial', 20, 'bold'),
            fg="#009BA7", bg='#000000'
        ).pack(side=tk.LEFT)

        self.status_var = tk.StringVar(value="Ready")
        tk.Label(
            header_frame,
            textvariable=self.status_var,
            font=('Arial', 12),
            fg='#FF8C00', bg='#000000'
        ).pack(side=tk.RIGHT)

        # ── Main content ──────────────────────────────────────────────────
        main_frame = tk.Frame(self.root, bg='#000000')
        main_frame.pack(fill=tk.BOTH, expand=True, padx=20, pady=10)

        # Left panel
        left_panel = tk.Frame(main_frame, bg='#000000')
        left_panel.pack(side=tk.LEFT, fill=tk.Y, padx=(0, 20))

        # Voice visualisation
        viz_frame = tk.Frame(left_panel, bg='#000000', relief=tk.FLAT,
                            highlightbackground="#1a1a1a", highlightthickness=1)
        viz_frame.pack(fill=tk.X, pady=(0, 20))
        self.viz_canvas = tk.Canvas(viz_frame, width=200, height=150,
                                    bg='#000000', highlightthickness=0)
        self.viz_canvas.pack(pady=10)
        self.draw_voice_visualization(0)

        # Listen button
        control_frame = tk.Frame(left_panel, bg='#000000')
        control_frame.pack(fill=tk.X, pady=(0, 20))
        self.listen_btn = tk.Button(
            control_frame, text="🎤 Start Listening",
            command=self.toggle_listening,
            font=('Arial', 12, 'bold'),
            bg='#87fefe', fg='#000000',
            activebackground='#FF8C00', activeforeground='#000000',
            relief=tk.FLAT, bd=0, padx=10, pady=5, cursor='hand2'
        )
        self.listen_btn.pack(pady=5)

        # System status
        status_frame = tk.Frame(left_panel, bg='#000000', relief=tk.FLAT,
                                highlightbackground='#009BA7', highlightthickness=1)
        status_frame.pack(fill=tk.X, pady=(0, 20))
        tk.Label(status_frame, text="System Status", font=('Arial', 12, 'bold'),
                fg='#87fefe', bg='#000000').pack(pady=5)
        self.cpu_label    = tk.Label(status_frame, text="CPU: --%",    font=('Arial', 10),
                                    fg='white', bg='#000000')
        self.memory_label = tk.Label(status_frame, text="Memory: --%", font=('Arial', 10),
                                    fg='white', bg='#000000')
        self.disk_label   = tk.Label(status_frame, text="Disk: --%",   font=('Arial', 10),
                                    fg='white', bg='#000000')
        self.cpu_label.pack(anchor=tk.W, padx=10)
        self.memory_label.pack(anchor=tk.W, padx=10)
        self.disk_label.pack(anchor=tk.W, padx=10)

        # Quick commands
        commands_frame = tk.Frame(left_panel, bg='#000000', relief=tk.FLAT,
                                highlightbackground='#009BA7', highlightthickness=1)
        commands_frame.pack(fill=tk.BOTH, expand=True)
        tk.Label(commands_frame, text="Quick Commands", font=('Arial', 12, 'bold'),
                fg='#009BA7', bg='#000000').pack(pady=5)

        quick_commands = [
            ("Open Chrome",    self.open_chrome),
            ("Open Notepad",   self.open_notepad),
            ("Open Brave",     self.open_brave),
            ("Volume Up",      self.volume_up),
            ("Volume Down",    self.volume_down),
            ("Brightness +",   self.brightness_up),
            ("Brightness -",   self.brightness_down),
            ("Take Screenshot",self.take_screenshot),
            ("System Info",    self.show_system_info),
            ("Create Folder",  self._gui_create_folder),
            ("Create File",    self._gui_create_file),
            ("Open Terminal",  self.open_terminal),
            ("Calculate",      self._gui_calculate),
        ]

        # WhatsApp button (standalone style)
        tk.Button(commands_frame, text="WhatsApp", command=self.open_whatsapp,
                font=('Arial', 10), bg='#222222', fg='white',
                activebackground='#87fefe', activeforeground='#000000',
                relief=tk.FLAT, bd=0, width=15, cursor='hand2').pack(pady=2)

        for cmd_text, cmd_func in quick_commands:
            tk.Button(
                commands_frame, text=cmd_text, command=cmd_func,
                font=('Arial', 10), bg='#222222', fg='white',
                activebackground='#87fefe', activeforeground='#000000',
                relief=tk.FLAT, bd=0, width=15, cursor='hand2'
            ).pack(pady=2)

        # Right panel
        right_panel = tk.Frame(main_frame, bg='#000000')
        right_panel.pack(side=tk.RIGHT, fill=tk.BOTH, expand=True)

        conv_frame = tk.Frame(right_panel, bg='#000000')
        conv_frame.pack(fill=tk.BOTH, expand=True)
        tk.Label(conv_frame, text="Conversation Log:", font=('Arial', 12, 'bold'),
                fg='white', bg='#000000').pack(anchor=tk.W)
        self.conversation_log = scrolledtext.ScrolledText(
            conv_frame, wrap=tk.WORD, width=60, height=15,
            bg="#000000", fg='#FF8C00', font=('Consolas', 10),
            insertbackground='#009BA7',
            relief=tk.SOLID,                  # solid border
            bd=1,                             # border width
            highlightbackground='#009BA7',    # border colour (orange)
            highlightthickness=1.5,             # border thickness
            highlightcolor='#009BA7'          # border when focused (brighter orange)
        )
        self.conversation_log.pack(fill=tk.BOTH, expand=True, pady=5)
        self.conversation_log.config(state=tk.DISABLED)
        self.conversation_log.tag_configure("bold", font=("Consolas", 10, "bold"),
                                            foreground='#87fefe')

        # App control panel
        self.app_control_frame = tk.Frame(right_panel, bg="#000000",
                                        relief=tk.FLAT, bd=0,
                                        highlightbackground="#1a1a1a", highlightthickness=1)
        self.app_control_frame.pack(fill=tk.X, pady=(10, 0))
        tk.Label(self.app_control_frame, text="Current Application: None",
                font=('Arial', 11, 'bold'), fg='#87fefe', bg='#000000').pack(pady=5)
        self.app_commands_text = tk.Text(
            self.app_control_frame, wrap=tk.WORD, width=60, height=4,
            bg='#000000', fg='#CCCCCC', font=('Arial', 9), state=tk.DISABLED,
            relief=tk.FLAT, bd=0, padx=10, pady=5
        )
        self.app_commands_text.pack(fill=tk.X, padx=10, pady=5)

        self.monitor_system()

        # LLM chat bar (multi‑line, taller input)
        llm_frame = tk.Frame(right_panel, bg='#000000')
        llm_frame.pack(fill=tk.X, pady=(10, 0))

        self.llm_entry = tk.Text(llm_frame, height=3, wrap=tk.WORD,
                                bg='#222222', fg='white',
                                insertbackground='#E65100', font=('Arial', 11),
                                relief=tk.FLAT, bd=1, padx=8, pady=8)
        self.llm_entry.pack(side=tk.LEFT, fill=tk.X, expand=True, padx=(0, 5))

        # Ctrl+Enter sends the message (keeps plain Enter for newline)
        self.llm_entry.bind("<Control-Return>", lambda e: self.ask_llm())

        self.llm_btn = tk.Button(llm_frame, text="Ask Xena", command=self.ask_llm,
                                bg='#E65100', fg='#000000', font=('Arial', 16, 'bold'),
                                relief=tk.FLAT, bd=0, padx=10, pady=3, cursor='hand2', height=2)
        self.llm_btn.pack(side=tk.RIGHT)

        # Webcam panel
        webcam_frame = tk.Frame(right_panel, bg='#000000', relief=tk.FLAT, bd=0,
                                highlightbackground='#009BA7', highlightthickness=1)
        webcam_frame.pack(fill=tk.BOTH, expand=True, pady=10)

        self.webcam_label = tk.Label(webcam_frame, bg="#000000")
        self.webcam_label.pack(pady=5)
        cam_btn_frame = tk.Frame(webcam_frame, bg='#000000')
        cam_btn_frame.pack(pady=5)

        self.cam_toggle_btn = tk.Button(
            cam_btn_frame, text="Start Camera",
            command=self.toggle_camera,
            font=('Arial', 12), bg='#87fefe', fg='#000000',
            activebackground='#FF8C00', activeforeground='#000000',
            relief=tk.FLAT, bd=0, width=15, cursor='hand2'
        )
        self.cam_toggle_btn.pack(side=tk.LEFT, padx=5)

        self.capture_btn = tk.Button(
            cam_btn_frame, text="Analyze Face",
            command=self.capture_analyze,
            font=('Arial', 12), bg='#222222', fg='white',
            activebackground='#87fefe', activeforeground='#000000',
            relief=tk.FLAT, bd=0, width=15, cursor='hand2'
        )
        self.capture_btn.pack(side=tk.LEFT, padx=5)

        self.root.after(50, self._poll_llm_queue)

    # ── GUI helper popups for new quick-command buttons ──────────────────────

    def _gui_create_folder(self):
        self._simple_input_dialog("Create Folder", "Folder name:", self.create_folder)

    def _gui_create_file(self):
        self._simple_input_dialog("Create File", "File name (e.g. notes.txt):", self.create_file)

    def _gui_calculate(self):
        self._simple_input_dialog("Calculate", "Expression (e.g. 15 plus 7):", self.calculate)

    def _simple_input_dialog(self, title, prompt, callback):
        dlg = tk.Toplevel(self.root)
        dlg.title(title)
        dlg.configure(bg='#000000')
        dlg.resizable(False, False)
        tk.Label(dlg, text=prompt, fg='white', bg='#000000',
                font=('Arial', 11)).pack(padx=15, pady=(15, 5))
        entry = tk.Entry(dlg, width=35, bg="#000000",
                        insertbackground='#87fefe', font=('Arial', 11))
        entry.pack(padx=15, pady=5)
        entry.focus_set()

        def _ok(event=None):
            val = entry.get().strip()
            dlg.destroy()
            if val:
                result = callback(val)
                self.add_to_log("Assistant", result)
                self.speak(result)

        tk.Button(dlg, text="OK", command=_ok,
                bg='#87fefe', fg='#000000',
                activebackground='#FF8C00', activeforeground='#000000',
                font=('Arial', 10, 'bold'), relief=tk.FLAT, bd=0,
                width=10, cursor='hand2').pack(pady=(5, 15))
        entry.bind('<Return>', _ok)

    # =========================================================================
    # VOICE VISUALISATION
    # =========================================================================

    def draw_voice_visualization(self, level):
        self.viz_canvas.delete("all")
        width, height = 200, 150
        cx, cy = width // 2, height // 2
        for i in range(5):
            radius = 20 + i * 15 + level * 5
            r = 80 + i * 30
            g = 30 + level * 10
            b = 0
            color = f'#{min(255, r):02x}{min(255, g):02x}{b:02x}'
            self.viz_canvas.create_oval(
                cx - radius, cy - radius,
                cx + radius, cy + radius,
                outline=color, width=2
            )

    # =========================================================================
    # LISTENING LOOP
    # =========================================================================

    def toggle_listening(self):
        if not self.is_listening:
            self.is_listening = True
            self.listen_btn.config(text="🔴 Stop Listening", bg='#cc0000')
            self.status_var.set("Listening...")
            self.add_to_log("System", "Voice recognition activated")
            t = threading.Thread(target=self.listen_loop, daemon=True)
            t.start()
        else:
            self.is_listening = False
            self.listen_btn.config(text="🎤 Start Listening", bg='#007acc')
            self.status_var.set("Ready")
            self.add_to_log("System", "Voice recognition deactivated")
            self.draw_voice_visualization(0)

    def listen_loop(self):
        if not self.mic_available:
            self.add_to_log("System", "Microphone unavailable – voice input disabled.")
            return

        import sounddevice as sd
        import numpy as np

        sample_rate = 16000   # Works fine with Google
        self.add_to_log("System", "Listening (Google Speech Recognition)...")

        while self.is_listening:
            if self.llm_busy:
                time.sleep(0.2)
                continue
            try:
                # Record 5 seconds of audio
                recording = sd.rec(int(5 * sample_rate), samplerate=sample_rate,
                                channels=1, dtype='int16')
                sd.wait()

                # Convert to SpeechRecognition AudioData
                audio_data = sr.AudioData(recording.tobytes(), sample_rate, 2)

                # Use Google's online recognition
                command = self.recognizer.recognize_google(audio_data).lower()
                self.add_to_log("You", command)
                self.process_command(command)

            except sr.UnknownValueError:
                self.add_to_log("System", "Listening...")
            except sr.RequestError as e:
                self.add_to_log("System", f"Google speech recognition error: {e}")
            except Exception as e:
                self.add_to_log("System", f"Listening error: {e}")

    # =========================================================================
    # COMMAND ROUTER
    # =========================================================================

    def process_command(self, command):
        """Route voice command to the appropriate handler."""
        response = ""

        # ── File / Folder management ────────────────────────────────────────

        if 'create folder' in command:
            name = command.replace('create folder', '').strip()
            response = self.create_folder(name) if name else \
                       "Please say the folder name after 'create folder'."

        elif 'create file' in command:
            name = command.replace('create file', '').strip()
            response = self.create_file(name) if name else \
                       "Please say the file name after 'create file'."

        # ── VS Code  ───────────────────────────────────────────────
        elif 'open folder' in command and 'vs code' in command:
            path = re.sub(r'open folder|in vs ?code', '', command).strip()
            response = self.open_in_vscode(path, is_folder=True)

        elif 'open file' in command and 'vs code' in command:
            path = re.sub(r'open file|in vs ?code', '', command).strip()
            response = self.open_in_vscode(path, is_folder=False)

        elif 'open notebook' in command or \
             ('open' in command and '.ipynb' in command):
            path = re.sub(r'open notebook|open', '', command).strip()
            response = self.open_in_jupyter(path)

        # ── Terminal / run command ──────────────────────────────────────────
        elif 'run command' in command:
            cmd_text = command.replace('run command', '').strip()
            response = self.run_terminal_command(cmd_text) if cmd_text else \
                       "Please say the command after 'run command'."

        elif 'open terminal' in command or 'open command prompt' in command or 'open cmd' in command or 'open powershell' in command:
            response = self.open_terminal()

        # ── Brave Browser ───────────────────────────────────────────────────
        elif 'open brave' in command:
            response = self.open_brave()

        elif 'new tab in brave' in command or 'open new tab brave' in command:
            response = self.brave_new_tab()

        elif 'incognito' in command and 'brave' in command:
            response = self.brave_incognito()

        elif 'close brave' in command:
            response = self.close_brave()

        # ── WhatsApp ─────────────────────────────────────────

        elif 'whatsapp' in command:
            response = self.open_whatsapp()

        # ── Math ────────────────────────────────────────────────────────────
        
        elif 'calculate' in command or \
             'compute' in command or \
             'square root of' in command or \
             re.search(r'\b(sin|cos|tan)\s+of\b', command):
            expr = re.sub(r'^(calculate|compute)', '', command).strip()
            response = self.calculate(expr)

        # ── Existing app control ────────────────────────────────────────────

        elif 'chrome' in command or 'browser' in command:
            response = self.control_application('chrome', command)

        elif 'notepad' in command or 'text editor' in command:
            response = self.control_application('notepad', command)

        elif 'file explorer' in command or 'files' in command:
            response = self.control_application('file explorer', command)

        elif 'vlc' in command or 'media player' in command or 'video' in command:
            response = self.control_application('vlc', command)

        # ── System commands ─────────────────────────────────────────────────
        elif 'shutdown' in command or 'restart' in command or 'sleep' in command or 'go to sleep' in command or 'lock' in command:
            response = self.control_system(command)

        # ── Basic app opens ─────────────────────────────────────────────────

        elif 'open calculator' in command:
            response = self.open_calculator()
        elif 'open task manager' in command:
            response = self.open_task_manager()

        # ── Media control ───────────────────────────────────────────────────

        elif 'volume up' in command or 'increase volume' in command:
            response = self.volume_up()
        elif 'volume down' in command or 'decrease volume' in command:
            response = self.volume_down()
        elif 'mute' in command:
            response = self.volume_mute()

        # ── Brightness ──────────────────────────────────────────────────────
        elif 'brightness up' in command or 'increase brightness' in command:
            response = self.brightness_up()
        elif 'brightness down' in command or 'decrease brightness' in command:
            response = self.brightness_down()

        # ── System info / screenshot ────────────────────────────────────────
        elif 'system info' in command or 'system information' in command:
            response = self.show_system_info()
        elif 'screenshot' in command:
            response = self.take_screenshot()

        # ── Time / date ─────────────────────────────────────────────────────
        elif 'time' in command:
            response = f"The current time is {datetime.datetime.now().strftime('%I:%M %p')}"
        elif 'date' in command:
            response = f"Today's date is {datetime.datetime.now().strftime('%A, %B %d, %Y')}"

        # ── Web search / navigation ─────────────────────────────────────────
        elif 'search online for' in command or 'find for' in command:
            query = command.replace('search online for', '').replace('find for', '').strip()
            if query:
                webbrowser.open(f"https://www.google.com/search?q={query}")
                response = f"Searching for {query}"
            else:
                response = "What would you like me to search for?"
        elif 'open website' in command or 'go to' in command:
            site = command.replace('open website', '').replace('go to', '').strip()
            if site:
                if not site.startswith('http'):
                    site = 'https://' + site
                webbrowser.open(site)
                response = f"Opening {site}"

        # ── In-app hotkeys ──────────────────────────────────────────────────

        elif 'new tab' in command or 'close tab' in command or 'refresh' in command or 'save' in command or 'copy' in command or 'paste' in command or 'select all' in command or 'undo' in command or 'find' in command or 'replace' in command:
            response = self.execute_app_command(command)

        # ── Shutdown assistant ──────────────────────────────────────────────

        elif 'exit' in command or 'quit' in command or 'goodbye' in command:
            response = "Shutting down Xena. Goodbye!"
            self.add_to_log("Assistant", response)
            self.speak(response)
            self.root.after(1000, self.root.destroy)
            return
        
        # ── Face / Emotion commands ─────────────────────

        elif 'start camera' in command or 'turn on camera' in command or 'open camera' in command:
            self.start_camera()
            response = "Camera started."
            
        elif 'stop camera' in command or 'turn off camera' in command:
            self.stop_camera()
            response = "Camera stopped."

        elif 'analyze face' in command or 'what is my emotion' in command:
            self.capture_analyze()
            response = "Analyzing your face..."

        elif 'recognize face' in command or 'who am i' in command:
            self.capture_analyze()   # reuse same logic
            response = "Checking identity..."

        elif 'toggle face recognition' in command or 'live recognition' in command:
            response = self.start_face_recognition_mode()

        else:
            self.llm_busy = True
            self.status_var.set("Thinking...")
            self.ask_llm(command)   # This will log the prompt and reply asynchronously
            return                

        self.add_to_log("Assistant", response)
        self.speak(response)

    # =========================================================================
    # NEW FEATURE 1 – File / Folder Management
    # =========================================================================

    def create_folder(self, folder_name: str) -> str:
        """Create a new folder in the current working directory."""
        try:
            # Sanitise name – strip path separators to avoid directory traversal
            folder_name = folder_name.strip().replace('/', '').replace('\\', '')
            if not folder_name:
                return "Please provide a valid folder name."
            target = os.path.join(os.path.expanduser("~"), "Desktop", folder_name)
            os.makedirs(target, exist_ok=True)
            return f"Folder '{folder_name}' created on the Desktop."
        except PermissionError:
            return f"Permission denied while creating folder '{folder_name}'."
        except Exception as e:
            return f"Error creating folder: {e}"

    def create_file(self, file_name: str) -> str:
        """Create a new empty file on the Desktop."""
        try:
            file_name = file_name.strip().replace('/', '').replace('\\', '')
            if not file_name:
                return "Please provide a valid file name."
            target = os.path.join(os.path.expanduser("~"), "Desktop", file_name)
            with open(target, 'x', encoding='utf-8'):  # 'x' fails if exists
                pass
            return f"File '{file_name}' created on the Desktop."
        except FileExistsError:
            return f"File '{file_name}' already exists."
        except PermissionError:
            return f"Permission denied while creating '{file_name}'."
        except Exception as e:
            return f"Error creating file: {e}"

    # =========================================================================
    # NEW FEATURE 2 – VS Code & Jupyter Notebook Integration
    # =========================================================================

    def _find_vscode(self) -> str | None:
        """Return the VS Code executable path or None."""
        # Common binary names
        for candidate in ('code', 'code-insiders'):
            if shutil.which(candidate):
                return candidate
        # Windows fallback paths
        if platform.system() == "Windows":
            for path in (
                os.path.expandvars(r"%LOCALAPPDATA%\Programs\Microsoft VS Code\Code.exe"),
                r"C:\Program Files\Microsoft VS Code\Code.exe",
            ):
                if os.path.isfile(path):
                    return path
        # macOS fallback
        if platform.system() == "Darwin":
            mac_path = "/Applications/Visual Studio Code.app/Contents/Resources/app/bin/code"
            if os.path.isfile(mac_path):
                return mac_path
        return None

    def open_in_vscode(self, path: str, is_folder: bool = False) -> str:
        """Open a file or folder in VS Code."""
        try:
            vscode = self._find_vscode()
            if not vscode:
                return ("VS Code not found. Make sure 'code' is in your PATH "
                        "or VS Code is installed.")
            path = path.strip() or os.path.expanduser("~")
            threading.Thread(
                target=lambda: subprocess.Popen([vscode, path]),
                daemon=True
            ).start()
            kind = "folder" if is_folder else "file"
            return f"Opening {kind} '{path}' in VS Code."
        except Exception as e:
            return f"Error opening VS Code: {e}"

    def open_in_jupyter(self, path: str) -> str:
        """Open a .ipynb notebook in Jupyter Notebook or Jupyter Lab."""
        try:
            # Try jupyter lab first, then notebook
            jupyter_cmd = None
            for candidate in ('jupyter-lab', 'jupyter lab',
                              'jupyter-notebook', 'jupyter notebook'):
                parts = candidate.split()
                if shutil.which(parts[0]):
                    jupyter_cmd = parts
                    break

            if not jupyter_cmd:
                return ("Jupyter not found. Install with: "
                        "pip install jupyterlab")

            path = path.strip()
            cmd = jupyter_cmd + ([path] if path else [])
            threading.Thread(
                target=lambda: subprocess.Popen(cmd),
                daemon=True
            ).start()
            return f"Launching Jupyter for '{path}'." if path else \
                   "Launching Jupyter Notebook."
        except Exception as e:
            return f"Error opening Jupyter: {e}"

    # =========================================================================
    # NEW FEATURE 3 – Terminal with Custom Command Execution
    # =========================================================================

    def open_terminal(self) -> str:
        """Open the system default terminal."""
        try:
            system = platform.system()
            if system == "Windows":
                # Try PowerShell first, fall back to cmd
                if shutil.which("powershell"):
                    subprocess.Popen(["powershell"])
                else:
                    subprocess.Popen(["cmd"])
            elif system == "Darwin":
                subprocess.Popen(["open", "-a", "Terminal"])
            else:
                # Linux – try common terminal emulators
                for term in ("gnome-terminal", "xterm", "konsole", "xfce4-terminal"):
                    if shutil.which(term):
                        subprocess.Popen([term])
                        break
                else:
                    return "No supported terminal emulator found."
            return "Opening terminal."
        except Exception as e:
            return f"Error opening terminal: {e}"

    def run_terminal_command(self, cmd_text: str) -> str:
        """Open a terminal window and execute a spoken command inside it."""
        try:
            system = platform.system()
            if system == "Windows":
                # start cmd and keep window open after command
                subprocess.Popen(
                    f'start cmd /k "{cmd_text}"', shell=True
                )
            elif system == "Darwin":
                # Use osascript to open Terminal and run the command
                script = (
                    f'tell application "Terminal" to do script "{cmd_text}"'
                )
                subprocess.Popen(["osascript", "-e", script])
            else:
                # Linux – gnome-terminal stays open after command via exec bash
                for term, flag in (
                    ("gnome-terminal", "--"),
                    ("xterm",          "-e"),
                    ("konsole",        "-e"),
                ):
                    if shutil.which(term):
                        subprocess.Popen(
                            [term, flag,
                             "bash", "-c", f"{cmd_text}; exec bash"]
                        )
                        break
                else:
                    return "No supported terminal emulator found."
            return f"Running command: {cmd_text}"
        except Exception as e:
            return f"Error running command: {e}"

    # =========================================================================
    # NEW FEATURE 4 – Brave Browser Control
    # =========================================================================

    def _brave_exe(self) -> list[str]:
        """Return the Brave browser command for the current OS."""
        system = platform.system()
        if system == "Windows":
            for path in (
                os.path.expandvars(
                    r"%LOCALAPPDATA%\BraveSoftware\Brave-Browser\Application\brave.exe"),
                r"C:\Program Files\BraveSoftware\Brave-Browser\Application\brave.exe",
            ):
                if os.path.isfile(path):
                    return [path]
            # Fallback – hope it is in PATH
            return ["brave"]
        elif system == "Darwin":
            return ["open", "-a", "Brave Browser"]
        else:
            for candidate in ("brave-browser", "brave"):
                if shutil.which(candidate):
                    return [candidate]
            return ["brave-browser"]

    def open_brave(self) -> str:
        try:
            threading.Thread(
                target=lambda: subprocess.Popen(self._brave_exe()),
                daemon=True
            ).start()
            self.current_app = 'brave'
            return "Opening Brave Browser."
        except Exception as e:
            return f"Error opening Brave: {e}"

    def brave_new_tab(self) -> str:
        try:
            cmd = self._brave_exe()
            if platform.system() == "Darwin":
                # macOS open -a doesn't support --new-tab directly
                subprocess.Popen(["open", "-a", "Brave Browser",
                                  "--args", "--new-tab"])
            else:
                subprocess.Popen(cmd + ["--new-tab"])
            return "Opening new tab in Brave."
        except Exception as e:
            return f"Error opening new tab in Brave: {e}"

    def brave_incognito(self) -> str:
        try:
            cmd = self._brave_exe()
            if platform.system() == "Darwin":
                subprocess.Popen(["open", "-a", "Brave Browser",
                                  "--args", "--incognito"])
            else:
                subprocess.Popen(cmd + ["--incognito"])
            return "Opening Brave in Incognito mode."
        except Exception as e:
            return f"Error opening Brave incognito: {e}"

    def close_brave(self) -> str:
        """Terminate all Brave browser processes safely."""
        try:
            killed = 0
            for proc in psutil.process_iter(['name', 'pid']):
                pname = (proc.info['name'] or '').lower()
                if 'brave' in pname:
                    proc.terminate()
                    killed += 1
            if killed:
                return f"Closed Brave ({killed} process(es) terminated)."
            return "Brave Browser is not running."
        except Exception as e:
            return f"Error closing Brave: {e}"


# ─────────────────────────────────────────────────
#  Face / Emotion Recognition Methods
# ─────────────────────────────────────────────────

    def toggle_camera(self):
        if not self.camera_running:
            self.start_camera()
        else:
            self.stop_camera()

    def start_camera(self):
        try:
            self.camera = cv2.VideoCapture(0)  # 0 = default webcam
            if not self.camera.isOpened():
                self.add_to_log("System", "Cannot open webcam.")
                return
            self.camera_running = True
            self.cam_toggle_btn.config(text="⏹️ Stop Camera", bg='#cc0000')
            self.update_webcam()  # start loop
            self.add_to_log("System", "Webcam started.")
        except Exception as e:
            self.add_to_log("System", f"Webcam error: {e}")

    def stop_camera(self):
        self.camera_running = False
        if self.camera:
            self.camera.release()
            self.camera = None
        self.webcam_label.config(image='')
        self.cam_toggle_btn.config(text="📷 Start Camera", bg='#007acc')
        self.add_to_log("System", "Webcam stopped.")

    def update_webcam(self):
        """Continuously grab frames and display them (optionally with detection)."""
        if not self.camera_running or self.camera is None:
            return
        ret, frame = self.camera.read()
        if ret:
            # If face recognition is enabled, run detection/recognition
            if self.face_recognition_enabled:
                frame = self.detect_faces(frame)

            # Convert OpenCV BGR to RGB and then to PIL/Tk
            rgb = cv2.cvtColor(frame, cv2.COLOR_BGR2RGB)
            img = Image.fromarray(rgb)
            imgtk = ImageTk.PhotoImage(image=img)
            self.webcam_label.imgtk = imgtk
            self.webcam_label.configure(image=imgtk)
        # Schedule next frame (30 fps)
        self.root.after(30, self.update_webcam)

    def detect_faces(self, frame):
        """Draw rectangles and labels for faces using OpenCV's DNN or Haar cascade."""
        # Use a simple Haar cascade for speed (or DNN for better accuracy)
        cascade = cv2.CascadeClassifier(cv2.data.haarcascades + 'haarcascade_frontalface_default.xml')
        gray = cv2.cvtColor(frame, cv2.COLOR_BGR2GRAY)
        faces = cascade.detectMultiScale(gray, 1.3, 5)

        for (x, y, w, h) in faces:
            cv2.rectangle(frame, (x, y), (x+w, y+h), (0, 255, 0), 2)
            # You can optionally run DeepFace on this region (slow) or just label "Unknown"
        return frame
    
    def preprocess_for_emotion(self, frame):
        """Convert to grayscale and apply CLAHE to boost contrast."""
        gray = cv2.cvtColor(frame, cv2.COLOR_BGR2GRAY)
        clahe = cv2.createCLAHE(clipLimit=3.0, tileGridSize=(8,8))
        enhanced = clahe.apply(gray)
        # Convert back to BGR for DeepFace (DeepFace expects a BGR image)
        return cv2.cvtColor(enhanced, cv2.COLOR_GRAY2BGR)

    def capture_analyze(self):
        if not self.camera or not self.camera_running:
            self.add_to_log("Assistant", "Please start the camera first.")
            return
        self.add_to_log("Assistant", "Capturing emotion burst (2 sec)...")
        threading.Thread(target=self._analyze_burst, daemon=True).start()

    def _analyze_burst(self):
        frames = []
        start = time.time()
        while time.time() - start < 2.0:
            ret, frame = self.camera.read()
            if ret:
                frames.append(frame.copy())
            time.sleep(0.1)
        if not frames:
            self.add_to_log("Assistant", "No frames captured.")
            return

        # Aggregate emotion scores
        emotion_sums = {}
        identity_votes = {}
        for f in frames:
            try:
                preprocessed = self.preprocess_for_emotion(f)
                temp = "temp_burst.jpg"
                cv2.imwrite(temp, preprocessed)
                res = DeepFace.analyze(temp, actions=['emotion'], enforce_detection=False,
                                   detector_backend='opencv', silent=True)
                emotions = res[0]['emotion']
                for k, v in emotions.items():
                    emotion_sums[k] = emotion_sums.get(k, 0) + v
                # Recognition (optional)
                try:
                    rec = DeepFace.find(temp, db_path=self.known_faces_dir,
                                        enforce_detection=False, silent=True)
                    if rec and not rec[0].empty:
                        ident = os.path.basename(os.path.dirname(rec[0]['identity'][0]))
                        identity_votes[ident] = identity_votes.get(ident, 0) + 1
                except:
                    pass
                os.remove(temp)
            except:
                continue

        if not emotion_sums:
            self.add_to_log("Assistant", "Could not analyze frames.")
            return

        # Average and find top emotion
        n = len(frames)
        avg_emotions = {k: v/n for k,v in emotion_sums.items()}
        sorted_emo = sorted(avg_emotions.items(), key=lambda x: x[1], reverse=True)
        dominant = sorted_emo[0][0]
        # If neutral is top but another emotion is within 10% of it, use that other emotion instead
        if dominant == 'neutral':
            runner_up = sorted_emo[1][0]
            if sorted_emo[1][1] > avg_emotions['neutral'] - 5:
                dominant = runner_up
                sorted_emo[0] = (runner_up, avg_emotions[runner_up])

        # Identity from majority vote
        identity = "Unknown"
        if identity_votes:
            identity = max(identity_votes, key=identity_votes.get)

        # Detailed response
        detail = ", ".join(f"{e}: {int(v)}%" for e,v in sorted_emo[:3])
        response = f"Over 2 seconds, you appear {dominant}. Top emotions: {detail}."
        if identity != "Unknown":
            response += f" Recognized as {identity}."
        self.add_to_log("Assistant", response)
        self.speak(response)

    def _analyze_frame(self, frame):
        """Run DeepFace for emotion and recognition, then report."""
        try:
            frame = self.preprocess_for_emotion(frame)
            temp_path = "temp_face.jpg"
            cv2.imwrite(temp_path, frame)

            # Use retinaface for better alignment (optional)
            emotion_result = DeepFace.analyze(
                img_path=temp_path,
                actions=['emotion'],
                enforce_detection=False,
                detector_backend='retinaface',  # better than opencv
                silent=True
            )
            # Save temp image for DeepFace
            temp_path = "temp_face.jpg"
            cv2.imwrite(temp_path, frame)

            # Emotion analysis
            emotion_result = DeepFace.analyze(img_path=temp_path, actions=['emotion'], enforce_detection=False)
            dominant_emotion = emotion_result[0]['dominant_emotion']
            emotion_detail = emotion_result[0]['emotion']

            # Face recognition (find identity from known_faces folder)
            identity = "Unknown"
            try:
                recognition_result = DeepFace.find(img_path=temp_path, db_path=self.known_faces_dir,
                                               enforce_detection=False, silent=True)
                if recognition_result and not recognition_result[0].empty:
                    identity = os.path.basename(os.path.dirname(recognition_result[0]['identity'][0]))
            except Exception:
                identity = "Unknown"

            # Build response
            response = f"I see a person who looks {dominant_emotion}."
            if identity != "Unknown":
                response += f" Recognized as {identity}."
            self.add_to_log("Assistant", response)
            self.speak(response)

            # Clean up
            os.remove(temp_path)
        except Exception as e:
            self.add_to_log("Assistant", f"Face analysis error: {e}")

    def start_face_recognition_mode(self):
        """Toggle live face recognition overlay."""
        self.face_recognition_enabled = not self.face_recognition_enabled
        state = "ON" if self.face_recognition_enabled else "OFF"
        self.add_to_log("System", f"Live face recognition turned {state}")
        return f"Live face recognition turned {state}."

    def add_known_face(self, name, image_path):
        """Copy a reference image into the known_faces folder for future recognition."""
        target_dir = os.path.join(self.known_faces_dir, name)
        os.makedirs(target_dir, exist_ok=True)
        shutil.copy(image_path, os.path.join(target_dir, os.path.basename(image_path)))
        return f"Added {name} to known faces."
    
    # =========================================================================
    # NEW FEATURE 5 – Sleep (enhanced / reliable)
    # =========================================================================

    def sleep_system(self) -> str:
        """Suspend / sleep the computer – reliable across platforms."""
        try:
            system = platform.system()
            if system == "Windows":
                # SetSuspendState(bHibernate, bForce, bWakeupEventsDisabled)
                subprocess.Popen(
                    ["powershell", "-Command",
                     "Add-Type -Assembly System.Windows.Forms; "
                     "[System.Windows.Forms.Application]::SetSuspendState("
                     "'Suspend', $false, $false)"]
                )
            elif system == "Darwin":
                subprocess.Popen(["pmset", "sleepnow"])
            else:
                subprocess.Popen(["systemctl", "suspend"])
            return "Putting system to sleep. Good night!"
        except Exception as e:
            return f"Error putting system to sleep: {e}"

    # =========================================================================        
    # NEW FEATURE 6 – WhatsApp Control (open desktop or web)
    # =========================================================================

    def open_whatsapp(self) -> str:
        """Open WhatsApp desktop app, or fall back to WhatsApp Web."""
        try:
            system = platform.system()
            # Try desktop app first
            if system == "Windows":
                # Common paths for WhatsApp on Windows
                possible_paths = [
                    os.path.expandvars(r"%LOCALAPPDATA%\WhatsApp\WhatsApp.exe"),
                    os.path.expandvars(r"%ProgramFiles%\WindowsApps\WhatsApp*.exe"),  # MS Store version
                    r"C:\Program Files\WhatsApp\WhatsApp.exe",
                ]
                for p in possible_paths:
                    # Handle wildcard for MS Store version
                    if '*' in p:
                        import glob
                        matches = glob.glob(p)
                        if matches:
                            subprocess.Popen([matches[0]])
                            return "Opening WhatsApp (Microsoft Store version)."
                    elif os.path.isfile(p):
                        subprocess.Popen([p])
                        return "Opening WhatsApp desktop app."
                # Fallback to web
                webbrowser.open("https://web.whatsapp.com")
                return "WhatsApp desktop not found. Opening WhatsApp Web."

            elif system == "Darwin":  # macOS
                apps = ["/Applications/WhatsApp.app", "/Applications/WhatsApp Desktop.app"]
                for app in apps:
                    if os.path.exists(app):
                        subprocess.Popen(["open", app])
                        return "Opening WhatsApp desktop app."
                webbrowser.open("https://web.whatsapp.com")
                return "Opening WhatsApp Web."

            else:  # Linux
                # Try the command 'whatsapp-desktop' or 'whatsapp'
                for cmd in ("whatsapp-desktop", "whatsapp"):
                    if shutil.which(cmd):
                        subprocess.Popen([cmd])
                        return f"Opening WhatsApp ({cmd})."
                webbrowser.open("https://web.whatsapp.com")
                return "Opening WhatsApp Web."

        except Exception as e:
            return f"Error opening WhatsApp: {e}"

    # =========================================================================
    # NEW FEATURE 6 – Mathematical Calculations (safe eval)
    # =========================================================================

    _MATH_NS = {
        '__builtins__': {},   # block all Python built-ins
        'pi':   math.pi,
        'e':    math.e,
        'sqrt': math.sqrt,
        'sin':  math.sin,
        'cos':  math.cos,
        'tan':  math.tan,
        'asin': math.asin,
        'acos': math.acos,
        'atan': math.atan,
        'log':  math.log,
        'log10':math.log10,
        'log2': math.log2,
        'ceil': math.ceil,
        'floor':math.floor,
        'abs':  abs,
        'pow':  math.pow,
        'exp':  math.exp,
    }

    def calculate(self, expression: str) -> str:
        """
        Evaluate a natural-language maths expression safely.

        Examples:
          "15 plus 7"              → 22
          "square root of 64"      → 8.0
          "sin of 30 degrees"      → 0.5
          "cos of pi radians"      → -1.0
          "2 to the power of 10"   → 1024.0
          "100 divided by 4"       → 25.0
        """
        try:
            expr = expression.lower().strip()

            # ── Natural language → Python ──────────────────────────────────

            # Square root
            expr = re.sub(r'square root of\s+', 'sqrt(', expr)

            # Trig with "of X degrees" → convert to radians
            def _trig_deg(m):
                fn  = m.group(1)
                val = m.group(2).strip()
                return f"{fn}(radians({val}))"

            expr = re.sub(
                r'\b(sin|cos|tan|asin|acos|atan)\s+of\s+(.+?)\s+degrees?\b',
                _trig_deg, expr
            )
            # Trig with "of X radians" (already radians)
            expr = re.sub(
                r'\b(sin|cos|tan|asin|acos|atan)\s+of\s+(.+?)\s+radians?\b',
                lambda m: f"{m.group(1)}({m.group(2).strip()})",
                expr
            )
            # Trig without unit – assume degrees
            expr = re.sub(
                r'\b(sin|cos|tan|asin|acos|atan)\s+of\s+(.+)',
                _trig_deg, expr
            )

            # Close any open sqrt( parentheses
            open_p = expr.count('(') - expr.count(')')
            expr += ')' * open_p

            # Arithmetic words
            replacements = [
                (r'\bplus\b',           '+'),
                (r'\bminus\b',          '-'),
                (r'\btimes\b',          '*'),
                (r'\bmultiplied by\b',  '*'),
                (r'\bdivided by\b',     '/'),
                (r'\bover\b',           '/'),
                (r'\bto the power of\b','**'),
                (r'\bexponent\b',       '**'),
                (r'\bmodulo\b',         '%'),
                (r'\bmod\b',            '%'),
                (r'\bpi\b',             'pi'),
            ]
            for pattern, replacement in replacements:
                expr = re.sub(pattern, replacement, expr)

            # Add radians helper to namespace
            ns = dict(self._MATH_NS)
            ns['radians'] = math.radians

            # Safety check – only allow safe characters
            if re.search(r'[^0-9\s\+\-\*\/\.\(\)\,\_a-z]', expr):
                return "Expression contains unsafe characters. Please rephrase."

            result = eval(expr, ns)  # noqa: S307 – namespace is sanitised above

            # Round floats nicely
            if isinstance(result, float):
                result = round(result, 10)
                if result == int(result):
                    result = int(result)

            return f"The result is {result}."
        except ZeroDivisionError:
            return "Cannot divide by zero."
        except Exception as e:
            return f"Could not calculate '{expression}': {e}"

    # =========================================================================
    # EXISTING APPLICATION CONTROL (unchanged)
    # =========================================================================

    def control_application(self, app_name, command):
        if f"open {app_name}" in command:
            if app_name == 'chrome':
                return self.open_chrome()
            elif app_name == 'notepad':
                return self.open_notepad()
            elif app_name == 'file explorer':
                return self.open_file_explorer()
            elif app_name == 'vlc':
                return self.open_vlc()
        self.current_app = app_name
        self.update_app_controls()
        return f"Ready to control {app_name}."

    def execute_app_command(self, command):
        if not self.current_app:
            return "No application is currently active."
        for cmd, action in self.app_commands.get(self.current_app, {}).items():
            if cmd in command:
                try:
                    action()
                    return f"Executed '{cmd}' in {self.current_app}."
                except Exception as e:
                    return f"Error executing '{cmd}': {e}"
        return f"Command not recognised for {self.current_app}."

    def control_system(self, command):
        if 'shutdown' in command:
            return self.shutdown_system()
        elif 'restart' in command:
            return self.restart_system()
        elif 'sleep' in command or 'go to sleep' in command:
            return self.sleep_system()
        elif 'lock' in command:
            return self.lock_screen()
        return "System command not recognised."

    # ── App openers ───────────────────────────────────────────────────────────

    def open_chrome(self):
        try:
            webbrowser.open("https://www.google.com")
            self.current_app = 'chrome'
            self.update_app_controls()
            return "Opening Google Chrome."
        except Exception as e:
            return f"Error opening Chrome: {e}"

    def open_notepad(self):
        try:
            if platform.system() == "Windows":
                subprocess.Popen(["notepad"])
            elif platform.system() == "Darwin":
                subprocess.Popen(["open", "-a", "TextEdit"])
            else:
                for editor in ("gedit", "mousepad", "xed", "nano"):
                    if shutil.which(editor):
                        subprocess.Popen([editor])
                        break
            self.current_app = 'notepad'
            self.update_app_controls()
            return "Opening Notepad / Text Editor."
        except Exception as e:
            return f"Error opening Notepad: {e}"

    def open_calculator(self):
        try:
            system = platform.system()
            if system == "Windows":
                subprocess.Popen(["calc"])
            elif system == "Darwin":
                subprocess.Popen(["open", "-a", "Calculator"])
            else:
                for calc in ("gnome-calculator", "kcalc", "xcalc"):
                    if shutil.which(calc):
                        subprocess.Popen([calc])
                        break
            return "Opening Calculator."
        except Exception as e:
            return f"Error opening Calculator: {e}"

    def open_file_explorer(self):
        try:
            system = platform.system()
            if system == "Windows":
                subprocess.Popen(["explorer"])
            elif system == "Darwin":
                subprocess.Popen(["open", "."])
            else:
                for fm in ("nautilus", "nemo", "thunar", "dolphin"):
                    if shutil.which(fm):
                        subprocess.Popen([fm])
                        break
            self.current_app = 'file explorer'
            self.update_app_controls()
            return "Opening File Explorer."
        except Exception as e:
            return f"Error opening File Explorer: {e}"

    def open_task_manager(self):
        try:
            system = platform.system()
            if system == "Windows":
                subprocess.Popen(["taskmgr"])
            elif system == "Darwin":
                subprocess.Popen(["open", "-a", "Activity Monitor"])
            else:
                subprocess.Popen(["gnome-system-monitor"])
            return "Opening Task Manager."
        except Exception as e:
            return f"Error opening Task Manager: {e}"

    def open_vlc(self):
        try:
            system = platform.system()
            if system == "Darwin":
                subprocess.Popen(["open", "-a", "VLC"])
            else:
                subprocess.Popen(["vlc"])
            self.current_app = 'vlc'
            self.update_app_controls()
            return "Opening VLC Media Player."
        except Exception as e:
            return f"Error opening VLC: {e}"

    # ── System control ────────────────────────────────────────────────────────

    def shutdown_system(self):
        try:
            system = platform.system()
            if system == "Windows":
                subprocess.Popen(["shutdown", "/s", "/t", "5"])
            else:
                subprocess.Popen(["shutdown", "-h", "now"])
            return "System will shut down in 5 seconds."
        except Exception as e:
            return f"Error shutting down: {e}"

    def restart_system(self):
        try:
            system = platform.system()
            if system == "Windows":
                subprocess.Popen(["shutdown", "/r", "/t", "5"])
            elif system == "Darwin":
                subprocess.Popen(["shutdown", "-r", "now"])
            else:
                subprocess.Popen(["reboot"])
            return "System will restart in 5 seconds."
        except Exception as e:
            return f"Error restarting: {e}"

    def lock_screen(self):
        try:
            system = platform.system()
            if system == "Windows":
                ctypes.windll.user32.LockWorkStation()
            elif system == "Darwin":
                subprocess.Popen(["pmset", "displaysleepnow"])
            else:
                subprocess.Popen(["gnome-screensaver-command", "-l"])
            return "Locking screen."
        except Exception as e:
            return f"Error locking screen: {e}"

    # ── Media control ─────────────────────────────────────────────────────────

    def volume_up(self):
        try:
            pyautogui.press('volumeup')
            return "Volume increased."
        except Exception as e:
            return f"Error: {e}"

    def volume_down(self):
        try:
            pyautogui.press('volumedown')
            return "Volume decreased."
        except Exception as e:
            return f"Error: {e}"

    def volume_mute(self):
        try:
            pyautogui.press('volumemute')
            return "Volume muted."
        except Exception as e:
            return f"Error: {e}"

    # ── Brightness ────────────────────────────────────────────────────────────

    def brightness_up(self):
        try:
            current = sbcpi.get_brightness()
            if isinstance(current, list):
                current = current[0]
            new = min(100, current + 10)
            sbcpi.set_brightness(new)
            return f"Brightness increased to {new}%."
        except Exception as e:
            return f"Error: {e}"

    def brightness_down(self):
        try:
            current = sbcpi.get_brightness()
            if isinstance(current, list):
                current = current[0]
            new = max(0, current - 10)
            sbcpi.set_brightness(new)
            return f"Brightness decreased to {new}%."
        except Exception as e:
            return f"Error: {e}"

    # ── System info / screenshot ──────────────────────────────────────────────

    def show_system_info(self):
        try:
            cpu  = psutil.cpu_percent(interval=1)
            mem  = psutil.virtual_memory()
            disk = psutil.disk_usage('/')
            return (
                f"System Information:\n"
                f"CPU: {cpu}%\n"
                f"Memory: {round(mem.used/1024**3,1)}GB / "
                f"{round(mem.total/1024**3,1)}GB ({mem.percent}%)\n"
                f"Disk: {round(disk.used/1024**3,1)}GB / "
                f"{round(disk.total/1024**3,1)}GB ({disk.percent}%)\n"
                f"OS: {platform.system()} {platform.release()}"
            )
        except Exception as e:
            return f"Error getting system info: {e}"

    def take_screenshot(self):
        try:
            fname = f"screenshot_{datetime.datetime.now().strftime('%Y%m%d_%H%M%S')}.png"
            pyautogui.screenshot().save(fname)
            return f"Screenshot saved as {fname}."
        except Exception as e:
            return f"Error taking screenshot: {e}"

    # ── App control panel ─────────────────────────────────────────────────────

    def update_app_controls(self):
        self.app_commands_text.config(state=tk.NORMAL)
        self.app_commands_text.delete(1.0, tk.END)
        if self.current_app:
            commands = self.app_commands.get(self.current_app, {})
            self.app_commands_text.insert(
                tk.END,
                f"Commands for {self.current_app}:\n" +
                "\n".join(f"• {c}" for c in commands)
            )
        else:
            self.app_commands_text.insert(
                tk.END, "No application active. Open an app to see commands."
            )
        self.app_commands_text.config(state=tk.DISABLED)

    # ── System monitor ────────────────────────────────────────────────────────

    def update_system_status(self):
        try:
            self.cpu_label.config(   text=f"CPU: {psutil.cpu_percent(interval=0.1)}%")
            self.memory_label.config(text=f"Memory: {psutil.virtual_memory().percent}%")
            self.disk_label.config(  text=f"Disk: {psutil.disk_usage('/').percent}%")
        except Exception as e:
            print(f"System status error: {e}")

    def monitor_system(self):
        self.update_system_status()
        self.root.after(2000, self.monitor_system)

    # ── TTS & log ─────────────────────────────────────────────────────────────

    def speak(self, text: str):
        """Non‑blocking: enqueue a text to be spoken by the dedicated thread."""
        if text and hasattr(self, 'speech_queue'):
            self.speech_queue.put(text)

    def _speech_worker(self):
        """Dedicated TTS thread – uses pyttsx3 with a started loop."""
        import time
        while self.speech_worker_running:
            try:
                text = self.speech_queue.get(timeout=1)
                if not text:
                    continue

                # Speak the text
                try:
                    self.engine.say(text)
                    # Wait until speaking finishes, but don’t block forever
                    start = time.time()
                    while self.engine.isBusy():
                        self.engine.iterate()
                        time.sleep(0.01)   # small sleep to avoid 100% CPU
                        if time.time() - start > 30:   # safety timeout
                            self.engine.stop()
                            break
                except Exception as e:
                    print(f"[TTS] Error while speaking: {e}")
                    # Try to reinitialise the engine if something went wrong
                    self._reinit_engine()

                time.sleep(0.1)

            except queue.Empty:
                continue

    def _reinit_engine(self):
        """Recreate the engine and restart the event loop."""
        try:
            self.engine = pyttsx3.init()
            current_rate = self.engine.getProperty('rate')
            self.engine.setProperty('rate', current_rate - 27)
            voices = self.engine.getProperty('voices')
            if len(voices) >= 2:
                self.engine.setProperty('voice', voices[1].id)
            elif voices:
                self.engine.setProperty('voice', voices[0].id)
            self.engine.startLoop(False)   # restart the loop
        except Exception as e:
            print(f"[TTS] Re‑init failed: {e}")

    def _engine_ok(self):
        """Quick check whether the engine is still responsive."""
        try:
            self.engine.getProperty('rate')
            return True
        except Exception:
            return False           

    def add_to_log(self, speaker: str, text: str):
        self.conversation_log.config(state=tk.NORMAL)
        ts = datetime.datetime.now().strftime("%H:%M:%S")
        self.conversation_log.insert(tk.END, f"[{ts}] {speaker}: {text}\n")
        self.conversation_log.see(tk.END)
        self.conversation_log.config(state=tk.DISABLED)


           # ──────────────── Local LLM (Ollama) ────────────────

    def ask_llm(self, prompt=None):
        if prompt is None:
            prompt = self.llm_entry.get("1.0", tk.END).strip()   # get all text
        if not prompt:
            return
        self.llm_entry.delete("1.0", tk.END)                     # clear the Text widget
        self.add_to_log("You (LLM)", prompt)
        self.llm_btn.config(state=tk.DISABLED)
        threading.Thread(target=self._llm_worker, args=(prompt,), daemon=True).start()

    def _llm_worker(self, prompt):
        full_response = []
        try:
            start_time = datetime.datetime.now().strftime("%H:%M:%S")
            self.llm_queue.put(("start", ("Xena", start_time)))
            r = requests.post(
                "http://localhost:11434/api/chat",
                json={
                    "model": "mistral:7b-instruct-q5_K_M",
                    "messages": [{"role": "user", "content": prompt}],
                    "stream": True,
                },
                stream=True,
                timeout=120,
            )
            r.raise_for_status()
            for line in r.iter_lines(decode_unicode=True):
                if not line:
                    continue
                try:
                    data = json.loads(line)
                    token = data.get("message", {}).get("content", "")
                    if token:
                        full_response.append(token)
                        self.llm_queue.put(("token", token))
                except json.JSONDecodeError:
                    continue

                self.llm_queue.put(("end", None))
        except Exception as e:
            self.llm_queue.put(("error", str(e)))
        finally:
            self.llm_queue.put(("done", "".join(full_response)))

    def _poll_llm_queue(self):
        try:
            while True:
                msg_type, data = self.llm_queue.get_nowait()
                if msg_type == "start":
                    speaker, timestamp = data
                    self.llm_current_speaker = speaker
                    self.llm_buffer = []
                    # No flag needed – we just append tokens as they come

                    self.conversation_log.config(state=tk.NORMAL)
                    if self.conversation_log.get("1.0", tk.END).strip():
                        self.conversation_log.insert(tk.END, "\n")
                    t = datetime.datetime.now().strftime("%H:%M:%S")
                    self.conversation_log.insert(tk.END, f"[{t}] {speaker}:\n")
                    self.conversation_log.config(state=tk.DISABLED)

                elif msg_type == "token":
                    self.llm_buffer.append(data)
                    self.conversation_log.config(state=tk.NORMAL)
                    self.conversation_log.insert(tk.END, data)   # No extra space
                    self.conversation_log.see(tk.END)
                    self.conversation_log.config(state=tk.DISABLED)

                elif msg_type == "end":
                    pass

                elif msg_type == "error":
                    self.add_to_log("LLM Error", data)
                    self._reset_llm_state()

                elif msg_type == "done":
                    full_response = data
                    self.conversation_log.config(state=tk.NORMAL)
                    self.conversation_log.insert(tk.END, "\n")
                    self.conversation_log.config(state=tk.DISABLED)
                    self.speak(full_response)
                    self._reset_llm_state()

        except queue.Empty:
            pass
        self.root.after(50, self._poll_llm_queue)

    def _reset_llm_state(self):
        """Reset LLM busy flag, buffer, and re-enable UI."""
        self.llm_busy = False
        self.llm_buffer = []
        self.llm_current_speaker = None
        self.llm_btn.config(state=tk.NORMAL)
        self.status_var.set("Ready")

# ──────────────────────Github Copilot Xena System (https://github.com/rubivssingh281-lab/Xena-AI-Assistant)──────────────
if __name__ == "__main__":
    root = tk.Tk()
    app  = AdvancedVoiceAssistant(root)
    root.mainloop()
