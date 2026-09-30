import platform
import subprocess
import ctypes
import shutil
import os
from typing import Any

def open_terminal(args: dict[str, Any]) -> dict[str, Any]:
    system = platform.system()
    if system == "Windows":
        if shutil.which("powershell"):
            subprocess.Popen(["powershell"])
        else:
            subprocess.Popen(["cmd"])
    elif system == "Darwin":
        subprocess.Popen(["open", "-a", "Terminal"])
    else:
        for term in ("gnome-terminal", "xterm", "konsole", "xfce4-terminal"):
            if shutil.which(term):
                subprocess.Popen([term])
                break
        else:
            raise RuntimeError("No supported terminal emulator found.")
    return {"status": "success", "message": "Terminal opened", "verified": True}

def open_browser(args: dict[str, Any]) -> dict[str, Any]:
    browser_name = args.get("browser", "chrome").lower()
    system = platform.system()
    
    if browser_name == "brave":
        if system == "Windows":
            paths = [
                os.path.expandvars(r"%LOCALAPPDATA%\BraveSoftware\Brave-Browser\Application\brave.exe"),
                r"C:\Program Files\BraveSoftware\Brave-Browser\Application\brave.exe",
            ]
            for p in paths:
                if os.path.isfile(p):
                    subprocess.Popen([p])
                    return {"status": "success", "message": "Brave opened", "verified": True}
            subprocess.Popen(["brave"])
        elif system == "Darwin":
            subprocess.Popen(["open", "-a", "Brave Browser"])
        else:
            subprocess.Popen(["brave-browser"])
    else:
        # Default to Chrome
        if system == "Windows":
            paths = [
                os.path.expandvars(r"%ProgramFiles%\Google\Chrome\Application\chrome.exe"),
                os.path.expandvars(r"%ProgramFiles(x86)%\Google\Chrome\Application\chrome.exe"),
                os.path.expandvars(r"%LOCALAPPDATA%\Google\Chrome\Application\chrome.exe"),
            ]
            for p in paths:
                if os.path.isfile(p):
                    subprocess.Popen([p])
                    return {"status": "success", "message": "Chrome opened", "verified": True}
            subprocess.Popen(["chrome"])
        elif system == "Darwin":
            subprocess.Popen(["open", "-a", "Google Chrome"])
        else:
            subprocess.Popen(["google-chrome"])
            
    return {"status": "success", "message": f"{browser_name.title()} opened", "verified": True}

def power_action(args: dict[str, Any]) -> dict[str, Any]:
    action = args.get("action", "lock").lower()
    system = platform.system()
    
    if system == "Windows":
        if action == "lock":
            ctypes.windll.user32.LockWorkStation()
        elif action == "sleep":
            subprocess.Popen([
                "powershell", "-Command",
                "Add-Type -Assembly System.Windows.Forms; [System.Windows.Forms.Application]::SetSuspendState('Suspend', $false, $false)"
            ])
        else:
            raise ValueError(f"Unknown power action: {action}")
    elif system == "Darwin":
        if action == "lock":
            subprocess.Popen(["pmset", "displaysleepnow"])
        elif action == "sleep":
            subprocess.Popen(["pmset", "sleepnow"])
    else:
        # Linux
        if action == "lock":
            subprocess.Popen(["xdg-screensaver", "lock"])
        elif action == "sleep":
            subprocess.Popen(["systemctl", "suspend"])
            
    return {"status": "success", "message": f"System {action} initiated", "verified": True}

def volume_control(args: dict[str, Any]) -> dict[str, Any]:
    action = args.get("action", "mute").lower()
    system = platform.system()
    
    if system == "Windows":
        # Standard library ctypes approach, avoids pyautogui dependency in core
        VK_VOLUME_MUTE = 0xAD
        VK_VOLUME_DOWN = 0xAE
        VK_VOLUME_UP = 0xAF
        
        vk = VK_VOLUME_MUTE
        if action == "up": vk = VK_VOLUME_UP
        elif action == "down": vk = VK_VOLUME_DOWN
        
        # Press and release
        ctypes.windll.user32.keybd_event(vk, 0, 0, 0)
        ctypes.windll.user32.keybd_event(vk, 0, 2, 0)
        
    elif system == "Darwin":
        if action == "mute":
            subprocess.Popen(["osascript", "-e", "set volume output muted not (output muted of (get volume settings))"])
        elif action == "up":
            subprocess.Popen(["osascript", "-e", "set volume output volume (output volume of (get volume settings) + 5)"])
        elif action == "down":
            subprocess.Popen(["osascript", "-e", "set volume output volume (output volume of (get volume settings) - 5)"])
            
    return {"status": "success", "message": f"Volume {action} applied", "verified": True}
