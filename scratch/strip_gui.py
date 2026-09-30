import re
import os

filepath = 'e:/Xena-AI/Xena.py'
with open(filepath, 'r', encoding='utf-8') as f:
    content = f.read()

# 1. Remove tkinter and PIL imports
content = re.sub(r'import tkinter as tk\n', '', content)
content = re.sub(r'from tkinter import scrolledtext\n', '', content)
content = re.sub(r'from PIL import Image, ImageTk\n', '', content)

# 2. Fix __init__
content = content.replace('def __init__(self, root):', 'def __init__(self):')
content = re.sub(r'self\.root = root\n\s+self\.root\.title\([^\)]+\)\n\s+self\.root\.geometry\([^\)]+\)\n\s+self\.root\.configure\([^\)]+\)\n', '', content)
content = content.replace('self.create_gui()', '')
content = content.replace('self.root.protocol("WM_DELETE_WINDOW", self.shutdown)', '')
content = re.sub(r'self\.root\.after\(\d+,\s*self\._poll_llm_queue\)', 'import threading; threading.Thread(target=self._poll_llm_queue_loop, daemon=True).start()', content)

# 3. Add a threaded loop for _poll_llm_queue
poll_loop = """    def _poll_llm_queue_loop(self):
        import time
        while True:
            self._poll_llm_queue()
            time.sleep(0.05)"""
content = content.replace('def _poll_llm_queue(self):', poll_loop + '\n\n    def _poll_llm_queue(self):')

# 4. Remove create_gui and all GUI building
# We can find the start of create_gui and remove everything until the next method
# But wait, create_gui has sub-functions. It's safer to just replace its body with `pass`.
# Actually, the user wants it ripped out.
# Let's replace the whole create_gui block. It ends at `def _gui_create_folder(self):` or similar.
gui_pattern = re.compile(r'    def create_gui\(self\):.*?    def _gui_create_folder\(self\):', re.DOTALL)
content = gui_pattern.sub('    def _gui_create_folder(self):', content)

# 5. Fix add_to_log
log_pattern = re.compile(r'    def add_to_log\(self, speaker, text\):.*?        self\.conversation_log\.see\(tk\.END\)', re.DOTALL)
content = log_pattern.sub('    def add_to_log(self, speaker, text):\n        print(f"[{speaker}] {text}")', content)

# 6. Fix draw_voice_visualization
viz_pattern = re.compile(r'    def draw_voice_visualization\(self, level\):.*?            \)', re.DOTALL)
content = viz_pattern.sub('    def draw_voice_visualization(self, level):\n        pass', content)

# 7. Strip out listen_btn config from toggle_listening
content = re.sub(r'self\.listen_btn\.config\([^)]+\)', 'pass', content)
content = re.sub(r'self\.status_var\.set\([^)]+\)', 'pass', content)

# 8. Fix update_webcam to avoid GUI updates
webcam_pattern = re.compile(r'    def update_webcam\(self\):.*?        self\.root\.after\(30, self\.update_webcam\)', re.DOTALL)
new_webcam = """    def update_webcam(self):
        if not self.camera_running or self.camera is None:
            return
        ret, frame = self.camera.read()
        if ret and self.face_recognition_enabled:
            self.detect_faces(frame)
        import threading
        threading.Timer(0.03, self.update_webcam).start()"""
content = webcam_pattern.sub(new_webcam, content)

# 9. Other root.after removals (if any)
# self.root.after(1000, self.root.destroy) -> import sys; sys.exit(0)
content = content.replace('self.root.after(1000, self.root.destroy)', 'import sys; sys.exit(0)')

# 10. Fix missing self.status_var definition in __init__ (we set it to pass above, but it might not exist)
# Actually, setting self.status_var = type('Dummy', (object,), {'set': lambda self, x: None})() in __init__ is safer for any missed calls
dummy_var = """        self.status_var = type('Dummy', (object,), {'set': lambda s, x: None})()
        self.listen_btn = type('Dummy', (object,), {'config': lambda s, **k: None})()"""
content = content.replace('self._llm_token_line_started = False', 'self._llm_token_line_started = False\n' + dummy_var)

# 11. Write back
with open(filepath, 'w', encoding='utf-8') as f:
    f.write(content)
print("Xena.py refactored successfully.")
