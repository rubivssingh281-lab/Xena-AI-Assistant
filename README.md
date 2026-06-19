# 🤖 Xena — Advanced Voice Assistant

> A feature-rich, AI-powered desktop voice assistant built with Python. Xena combines voice recognition, text-to-speech, a local LLM (via Ollama), real-time face/emotion recognition, system control, and much more — all wrapped in a sleek dark-themed Tkinter GUI.

---

## 📋 Table of Contents

1. [Features Overview](#features-overview)
2. [System Requirements](#system-requirements)
3. [Dependencies](#dependencies)
4. [Installation](#installation)
   - [Step 1 — Clone the Repository](#step-1--clone-the-repository)
   - [Step 2 — Create a Virtual Environment](#step-2--create-a-virtual-environment)
   - [Step 3 — Install Python Packages](#step-3--install-python-packages)
   - [Step 4 — Install Ollama & Pull the LLM Model](#step-4--install-ollama--pull-the-llm-model)
   - [Step 5 — Platform-Specific Notes](#step-5--platform-specific-notes)
5. [Running Xena](#running-xena)
6. [Voice Commands Reference](#voice-commands-reference)
7. [GUI Quick Commands](#gui-quick-commands)
8. [LLM Chat (Ask Xena)](#llm-chat--ask-xena)
9. [Face & Emotion Recognition](#face--emotion-recognition)
10. [Known Faces Setup](#known-faces-setup)
11. [Project Structure](#project-structure)
12. [Troubleshooting](#troubleshooting)

---

## ✨ Features Overview

| Category | Capabilities |
|---|---|
| **Voice Control** | Google Speech Recognition, 5-second recording bursts |
| **Text-to-Speech** | pyttsx3 offline TTS, female voice, custom speed |
| **Local LLM** | Ollama + Mistral 7B, streaming token-by-token output |
| **Face Recognition** | DeepFace identity matching from a `known_faces/` directory |
| **Emotion Detection** | 2-second burst analysis, dominant + top-3 emotions |
| **System Control** | Shutdown, restart, sleep, lock screen |
| **App Control** | Chrome, Notepad, VLC, File Explorer, Calculator, Task Manager |
| **Brave Browser** | Open, new tab, incognito, close |
| **WhatsApp** | Desktop app or WhatsApp Web fallback |
| **Volume & Brightness** | Increase/decrease/mute via hotkeys and system APIs |
| **File Management** | Create folders and files directly on your Desktop |
| **Developer Tools** | VS Code launcher, Jupyter Notebook launcher, terminal opener |
| **Terminal Commands** | Run shell commands by voice in a new terminal window |
| **Calculator** | Safe eval with natural language (e.g. "square root of 64") |
| **Web Search** | Google search via voice or text |
| **Screenshots** | Timestamped PNG saved to working directory |
| **System Monitor** | Live CPU, RAM, and Disk stats in the sidebar |

---

## 💻 System Requirements

- **Python:** 3.10 or higher (3.11 recommended)
- **OS:** Windows 10/11, macOS 12+, or Ubuntu 20.04+ (Linux)
- **RAM:** 8 GB minimum; 16 GB recommended (for the 7B LLM model)
- **Webcam:** Required for face/emotion recognition features
- **Microphone:** Required for voice recognition
- **Internet:** Required for Google Speech Recognition; everything else is local

---

## 📦 Dependencies

### Python Packages

| Package | Purpose | pip install command |
|---|---|---|
| `SpeechRecognition` | Google Speech-to-Text wrapper | `pip install SpeechRecognition` |
| `sounddevice` | Low-level audio recording | `pip install sounddevice` |
| `pyttsx3` | Offline text-to-speech | `pip install pyttsx3` |
| `requests` | HTTP calls to Ollama API | `pip install requests` |
| `psutil` | CPU / memory / disk stats | `pip install psutil` |
| `pyautogui` | Keyboard hotkeys & screenshots | `pip install pyautogui` |
| `screen-brightness-control` | Brightness get/set | `pip install screen-brightness-control` |
| `Pillow` | Image processing for webcam feed | `pip install Pillow` |
| `opencv-python` | Webcam capture & Haar cascade face detection | `pip install opencv-python` |
| `deepface` | Emotion detection & face recognition | `pip install deepface` |
| `numpy` | Array operations for audio & image | `pip install numpy` |
| `tf-keras` | DeepFace backend (Keras 3 compatibility) | `pip install tf-keras` |
| `tensorflow` | DeepFace model backend | `pip install tensorflow` (CPU) or `tensorflow-gpu` |

> **Note:** `tkinter` ships with the standard Python installer on Windows and macOS. On Linux you may need to install it separately (see [Step 5](#step-5--platform-specific-notes)).

### External Tools

| Tool | Purpose | Download |
|---|---|---|
| **Ollama** | Runs the local LLM server on `localhost:11434` | https://ollama.com |
| **Mistral 7B** | The language model used by Xena | Pulled via `ollama pull` command |
| **Brave Browser** *(optional)* | Required only for Brave-specific commands | https://brave.com |
| **VS Code** *(optional)* | Required only for VS Code launch commands | https://code.visualstudio.com |
| **Jupyter** *(optional)* | Required only for notebook launch commands | `pip install jupyterlab` |
| **VLC** *(optional)* | Required only for VLC media control commands | https://www.videolan.org |

---

## 🚀 Installation

### Step 1 — Clone the Repository

```bash
git clone https://github.com/rubivssingh281-lab/Xena-AI-Assistant.git
cd Xena-AI-Assistant
```

Or, if you have the file directly:

```bash
mkdir xena-assistant
cd xena-assistant
# Place xena.py inside this folder
```

---

### Step 2 — Create a Virtual Environment

It is strongly recommended to use a virtual environment to avoid dependency conflicts.

**Windows:**
```bash
python -m venv venv
venv\Scripts\activate
```

**macOS / Linux:**
```bash
python3 -m venv venv
source venv/bin/activate
```

---

### Step 3 — Install Python Packages

Install all dependencies in one go using the command below, or individually using the table above.

```bash
pip install --upgrade pip

pip install SpeechRecognition sounddevice pyttsx3 requests psutil pyautogui \
            screen-brightness-control Pillow opencv-python deepface numpy tf-keras
```

**Install TensorFlow (choose one):**

```bash
# CPU only (works on all machines)
pip install tensorflow

# GPU support (requires CUDA + cuDNN — for faster DeepFace inference)
pip install tensorflow-gpu
```

**Optional developer tools:**

```bash
# Jupyter Lab (for notebook launch feature)
pip install jupyterlab
```

**Verify your installation:**
```bash
python -c "import cv2, deepface, speech_recognition, pyttsx3, pyautogui, psutil; print('All core packages OK')"
```

---

### Step 4 — Install Ollama & Pull the LLM Model

Xena uses **Ollama** to run a local Mistral 7B LLM for open-ended questions and conversation.

**1. Download and install Ollama:**

- **Windows / macOS:** Download the installer from https://ollama.com/download
- **Linux:**
  ```bash
  curl -fsSL https://ollama.com/install.sh | sh
  ```

**2. Start the Ollama server:**
```bash
ollama serve
```

> Ollama will listen on `http://localhost:11434` by default. Leave this terminal open while Xena is running, or configure Ollama to start as a system service.

**3. Pull the Mistral model (one-time download, ~4.5 GB):**
```bash
ollama pull mistral:7b-instruct-q5_K_M
```

**4. Verify the model is available:**
```bash
ollama list
# Should show: mistral:7b-instruct-q5_K_M
```

---

### Step 5 — Platform-Specific Notes

#### 🪟 Windows

- `pyttsx3` uses the built-in SAPI5 voices. At least one voice (David / Zira) is available by default.
- If Zira (female voice, index 1) is not found, Xena will fall back to the first available voice.
- Brightness control requires the monitor to support WMI. On some setups it may return an error — this is non-fatal.
- PyAutoGUI may need **`pywin32`** on some Windows versions:
  ```bash
  pip install pywin32
  ```

#### 🍎 macOS

- Install the `portaudio` dependency for `sounddevice`:
  ```bash
  brew install portaudio
  pip install sounddevice
  ```
- Grant microphone and camera permissions to Terminal (or your IDE) in **System Settings → Privacy & Security**.
- Grant accessibility permissions for `pyautogui` hotkeys: **System Settings → Privacy & Security → Accessibility**.
- `pyttsx3` uses the built-in `say` engine; macOS voices (e.g., Samantha) will be used.

#### 🐧 Linux (Ubuntu / Debian)

**Tkinter:**
```bash
sudo apt update
sudo apt install python3-tk
```

**Audio (sounddevice + pyttsx3 eSpeak):**
```bash
sudo apt install portaudio19-dev espeak espeak-data libespeak-dev ffmpeg
pip install sounddevice pyttsx3
```

**Screen brightness:**
```bash
# ddcutil or xrandr must be installed for brightness control on Linux
sudo apt install ddcutil
# or
sudo apt install x11-xserver-utils
```

**Camera permissions:**
```bash
sudo usermod -a -G video $USER
# Log out and back in for the change to take effect
```

---

## ▶️ Running Xena

**1. Make sure Ollama is running** (in a separate terminal):
```bash
ollama serve
```

**2. Activate your virtual environment** (if not already active):
```bash
# Windows
venv\Scripts\activate

# macOS / Linux
source venv/bin/activate
```

**3. Launch Xena:**
```bash
python xena.py
```

The Xena GUI will open. Click **"🎤 Start Listening"** to activate voice recognition, or type directly into the **"Ask Xena"** text bar at the bottom of the window.

---

## 🎙️ Voice Commands Reference

Speak any of the following phrases after clicking **Start Listening**.

### 📁 File & Folder Management

| Say | Action |
|---|---|
| `"create folder my project"` | Creates a folder named `my project` on the Desktop |
| `"create file notes.txt"` | Creates an empty `notes.txt` on the Desktop |

### 🌐 Web & Browser

| Say | Action |
|---|---|
| `"search online for Python tutorials"` | Opens a Google search |
| `"go to github.com"` | Navigates to the specified URL |
| `"open chrome"` | Opens Google Chrome / default browser |

### 💜 Brave Browser

| Say | Action |
|---|---|
| `"open brave"` | Launches Brave Browser |
| `"new tab in brave"` | Opens a new tab in Brave |
| `"incognito brave"` | Opens Brave in private/incognito mode |
| `"close brave"` | Terminates all Brave processes |

### 💬 WhatsApp

| Say | Action |
|---|---|
| `"whatsapp"` | Opens WhatsApp Desktop or WhatsApp Web as fallback |

### 🖥️ Application Control (Hotkeys)

First, open or switch to the app, then say these while it is active:

| Say | Action |
|---|---|
| `"new tab"` | Ctrl+T (Chrome/Brave) |
| `"close tab"` | Ctrl+W |
| `"refresh"` | Ctrl+R |
| `"save"` | Ctrl+S |
| `"find"` | Ctrl+F |
| `"replace"` | Ctrl+H |
| `"undo"` | Ctrl+Z |
| `"copy"` | Ctrl+C |
| `"paste"` | Ctrl+V |
| `"select all"` | Ctrl+A |

### 📝 Open Applications

| Say | Action |
|---|---|
| `"open notepad"` | Opens Notepad / TextEdit / gedit |
| `"open calculator"` | Opens system calculator |
| `"open task manager"` | Opens Task Manager / Activity Monitor |
| `"open terminal"` | Opens PowerShell, Terminal, or gnome-terminal |
| `"open file explorer"` | Opens Explorer / Finder / Nautilus |
| `"open vlc"` | Opens VLC Media Player |

### 🧑‍💻 Developer Tools

| Say | Action |
|---|---|
| `"open folder /path/to/folder in vs code"` | Opens a folder in VS Code |
| `"open file main.py in vs code"` | Opens a file in VS Code |
| `"open notebook analysis.ipynb"` | Launches Jupyter for a notebook |
| `"run command pip install requests"` | Runs a command in a new terminal window |

### 🔊 Volume Control

| Say | Action |
|---|---|
| `"volume up"` / `"increase volume"` | Presses the Volume Up key |
| `"volume down"` / `"decrease volume"` | Presses the Volume Down key |
| `"mute"` | Presses the Mute key |

### ☀️ Brightness Control

| Say | Action |
|---|---|
| `"brightness up"` / `"increase brightness"` | Increases brightness by 10% |
| `"brightness down"` / `"decrease brightness"` | Decreases brightness by 10% |

### ⚙️ System Control

| Say | Action |
|---|---|
| `"shutdown"` | Shuts down the computer in 5 seconds |
| `"restart"` | Restarts the computer in 5 seconds |
| `"sleep"` / `"go to sleep"` | Puts the computer to sleep |
| `"lock"` | Locks the screen |
| `"system info"` | Speaks CPU, RAM, disk, and OS information |
| `"screenshot"` | Saves a timestamped screenshot as PNG |

### 🕐 Time & Date

| Say | Action |
|---|---|
| `"time"` | Tells you the current time |
| `"date"` | Tells you today's date |

### 📷 Camera & Face Recognition

| Say | Action |
|---|---|
| `"start camera"` / `"open camera"` | Activates the webcam feed |
| `"stop camera"` / `"turn off camera"` | Deactivates the webcam |
| `"analyze face"` / `"what is my emotion"` | Runs a 2-second emotion burst analysis |
| `"recognize face"` / `"who am i"` | Attempts face identity recognition |
| `"toggle face recognition"` / `"live recognition"` | Toggles live detection overlay on the feed |

### 🧮 Calculator (Natural Language)

| Say | Result |
|---|---|
| `"calculate 15 plus 7"` | 22 |
| `"calculate square root of 64"` | 8 |
| `"calculate sin of 30 degrees"` | 0.5 |
| `"calculate cos of pi radians"` | -1 |
| `"calculate 2 to the power of 10"` | 1024 |
| `"calculate 100 divided by 4"` | 25.0 |
| `"compute 15 modulo 4"` | 3 |

### 🚪 Exit

| Say | Action |
|---|---|
| `"exit"` / `"quit"` / `"goodbye"` | Gracefully shuts down Xena |

---

## 🖱️ GUI Quick Commands

These buttons are available in the left sidebar without needing to speak:

| Button | Action |
|---|---|
| **WhatsApp** | Opens WhatsApp desktop or web |
| **Open Chrome** | Opens Chrome / default browser |
| **Open Notepad** | Opens text editor |
| **Open Brave** | Launches Brave Browser |
| **Volume Up / Down** | Adjusts system volume |
| **Brightness + / -** | Adjusts screen brightness |
| **Take Screenshot** | Saves timestamped PNG |
| **System Info** | Logs CPU, RAM, disk, and OS details |
| **Create Folder** | Opens a dialog to enter a folder name |
| **Create File** | Opens a dialog to enter a file name |
| **Open Terminal** | Opens the system terminal |
| **Calculate** | Opens a dialog to enter a math expression |

---

## 🤖 LLM Chat — Ask Xena

At the bottom of the right panel, there is a text input bar for chatting with the local Mistral LLM:

1. Make sure **Ollama is running** (`ollama serve` in a terminal).
2. Type any question into the **"Ask Xena"** text box.
3. Press **Enter** or click **"Ask Xena"**.
4. Xena will stream the response **token by token** into the conversation log, and then speak the full response aloud when complete.

Unrecognised voice commands are also automatically routed to the LLM.

**Example questions:**
- `"Explain how neural networks work in simple terms"`
- `"Write a Python function to reverse a string"`
- `"What is the capital of Australia?"`
- `"Give me a recipe for pasta carbonara"`

---

## 😊 Face & Emotion Recognition

### Starting Live Camera
1. Click **"📷 Start Camera"** in the webcam panel (bottom-right).
2. The live feed will appear in the panel below.

### Analyzing Emotions
1. Click **"🔍 Analyze Face"** (or say `"analyze face"`).
2. Xena records a **2-second burst** of frames and averages the emotion scores.
3. Results are logged and spoken aloud: e.g., *"Over 2 seconds, you appear happy. Top emotions: happy: 72%, neutral: 18%, surprised: 10%."*

### Live Detection Overlay
- Say `"toggle face recognition"` or call the function to draw **green rectangles** around detected faces in the live feed using OpenCV's Haar cascade.

---

## 🧑 Known Faces Setup

To enable **identity recognition**, add reference photos for people Xena should recognise:

**Method 1 — Manually:**
```
known_faces/
├── Alice/
│   ├── alice_photo1.jpg
│   └── alice_photo2.jpg
└── Bob/
    └── bob_photo.jpg
```

Place one or more clear face photos inside a folder named after the person, inside the `known_faces/` directory. DeepFace will index these automatically on first use.

**Method 2 — Programmatically:**
```python
app.add_known_face("Alice", "/path/to/alice.jpg")
```

This copies the image into `known_faces/Alice/` automatically.

> **Tip:** Use well-lit, front-facing photos for best recognition accuracy. Multiple photos per person improve matching.

---

## 🗂️ Project Structure

```
xena-assistant/
│
├── xena.py                  # Main application file
├── README.md                # This file
├── requirements.txt         # (recommended — see below)
│
├── known_faces/             # Auto-created; add sub-folders per person
│   ├── Alice/
│   └── Bob/
│
└── screenshots/             # Screenshots saved here (timestamped .png)
```

### Recommended `requirements.txt`

Create this file in your project root for easy reinstalls:

```
SpeechRecognition>=3.10.0
sounddevice>=0.4.6
pyttsx3>=2.90
requests>=2.31.0
psutil>=5.9.0
pyautogui>=0.9.54
screen-brightness-control>=0.23.0
Pillow>=10.0.0
opencv-python>=4.8.0
deepface>=0.0.93
numpy>=1.24.0
tf-keras>=2.16.0
tensorflow>=2.16.0
```

Then install with:
```bash
pip install -r requirements.txt
```

---

## 🛠️ Troubleshooting

### "No TTS voices found" or Xena is silent
- **Windows:** Open **Settings → Time & Language → Speech** and verify at least one voice is installed (David or Zira).
- **macOS:** Open **System Settings → Accessibility → Spoken Content** and check voice availability.
- **Linux:** Run `sudo apt install espeak espeak-data` and restart.

### "Microphone unavailable – voice input disabled"
- Check that your microphone is connected and set as the default input device.
- On Linux: `sudo apt install portaudio19-dev` then `pip install sounddevice`.
- On macOS: Grant microphone permission to Terminal in **System Settings → Privacy → Microphone**.

### "Google speech recognition error"
- Xena uses Google's free Speech Recognition API which requires an internet connection.
- Check your network connection and try again.

### Ollama / LLM not responding
- Make sure you have started Ollama with `ollama serve` **before** launching Xena.
- Verify the model is present: `ollama list`
- If the model is missing: `ollama pull mistral:7b-instruct-q5_K_M`
- Check Ollama is accessible: `curl http://localhost:11434/api/tags`

### Webcam not opening
- Ensure no other application (e.g., Zoom, Teams) is using the camera.
- On Linux: make sure your user is in the `video` group: `sudo usermod -a -G video $USER`
- Try changing the camera index in the code: `cv2.VideoCapture(1)` instead of `0`.

### DeepFace / TensorFlow errors on first run
- DeepFace downloads its model weights (~200 MB) on first use. Ensure you have an internet connection for the first `analyze face` call.
- If you see a TensorFlow/Keras version conflict, install the compatibility shim: `pip install tf-keras`

### Brightness control not working
- **Windows:** WMI brightness control only works on laptop screens; external monitors are typically unsupported.
- **Linux:** Try installing `ddcutil` for external monitor support: `sudo apt install ddcutil`

### pyautogui hotkeys not working on macOS
- Go to **System Settings → Privacy & Security → Accessibility** and add your terminal or Python launcher to the allowed apps list.

---

## 📄 License

This project is open source. See the source file header for the original repository link:

```
https://github.com/rubivssingh281-lab/Xena-AI-Assistant
```

---
MIT License

Copyright (c) 2025 Saksham Singh

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.


Built with Python, powered by Ollama + Mistral, DeepFace, and Google Speech Recognition.
Author: Saksham Singh
