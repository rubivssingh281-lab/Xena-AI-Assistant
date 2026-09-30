"""
xena_face.py - accurate face recognition for Xena.

Pipeline for every camera frame:
  1. YuNet (OpenCV) finds each face plus 5 landmarks (eyes, nose tip, mouth corners).
  2. Each face is aligned to the standard 112x112 ArcFace pose with a similarity
     transform, so the recogniser always sees faces the same way.
  3. ArcFace ResNet-50 (InsightFace "buffalo_l", trained on WebFace600K) turns the
     aligned face into a 512-number embedding.
  4. The embedding is compared (cosine similarity) with every enrolled photo of
     every known person.
  5. A name is only shown when the match is above threshold, clearly ahead of the
     next-best person, and agreed on by several recent frames (voting), so a single
     blurry or turned frame can't mislabel anyone.

Known people live in known_faces/<Name>/*.jpg. Photos can be added by hand or with
"remember my face as <name>", which captures a set of good, varied samples.
"""
from __future__ import annotations

import json
import math
import os
import queue
import re
import threading
import time
from collections import Counter, deque
from dataclasses import dataclass, field
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

import cv2
import numpy as np

BASE_DIR = os.path.dirname(os.path.abspath(__file__))
MODELS_DIR = os.path.join(BASE_DIR, "models")
SETTINGS_PATH = os.path.join(MODELS_DIR, "face_settings.json")
GALLERY_CACHE = os.path.join(MODELS_DIR, "face_gallery_arcface.json")

YUNET_FILE = "face_detection_yunet_2023mar.onnx"
ARCFACE_FILE = "arcface_w600k_r50.onnx"
MODEL_SOURCES = {
    YUNET_FILE: [
        "https://github.com/opencv/opencv_zoo/raw/main/models/face_detection_yunet/face_detection_yunet_2023mar.onnx",
        "https://huggingface.co/opencv/face_detection_yunet/resolve/main/face_detection_yunet_2023mar.onnx",
    ],
    ARCFACE_FILE: [
        "https://huggingface.co/immich-app/buffalo_l/resolve/main/recognition/model.onnx",
    ],
}

# Where the five landmarks sit in a 112x112 ArcFace-aligned face.
ARCFACE_TEMPLATE = np.array([[38.2946, 51.6963], [73.5318, 51.5014], [56.0252, 71.7366],
                             [41.5493, 92.3655], [70.7299, 92.2041]], dtype=np.float32)

# Decision rules (cosine similarity between ArcFace embeddings). Measured on 30
# real photos of 6 people: same person, different photos = 0.35-0.97 (median 0.71);
# different people = at most 0.17. A frame only votes for someone above
# MATCH_THRESHOLD and at least MIN_MARGIN ahead of the next-best *different* person.
MATCH_THRESHOLD = 0.36
MIN_MARGIN = 0.08
VOTE_WINDOW = 8          # recent recognitions remembered per tracked face
MIN_VOTES = 3            # agreeing frames needed before a name (or "Unknown") is shown
RECOGNISE_EVERY = 0.25   # seconds between recognitions of the same tracked face

PREVIEW_WIDTH = 480
IMAGE_EXTS = (".jpg", ".jpeg", ".png", ".bmp", ".webp")

BLOCKED_MESSAGE = (
    "The camera is only sending a blank privacy image, so I can't see anyone. Your camera "
    "privacy switch or shutter seems to be on: on Lenovo laptops that's a key with a "
    "crossed-out camera icon (often F8 or F9) or a small slider above the screen. "
    "Turn it off and I'll start recognising faces right away.")

# Preview colours (BGR), matching the app's theme.
CYAN = (223, 215, 25)
ORANGE = (24, 106, 255)
GREY = (150, 150, 150)


def similarity_to_confidence(sim: float) -> float:
    """Map an ArcFace similarity to a 0-1 confidence (0.35 -> 50%, 0.5 -> 89%, 0.6 -> 97%)."""
    return 1.0 / (1.0 + math.exp(-(sim - 0.35) * 14.0))


def read_image(path: str):
    """cv2.imread that also works with non-ASCII Windows paths."""
    try:
        return cv2.imdecode(np.fromfile(path, dtype=np.uint8), cv2.IMREAD_COLOR)
    except Exception:
        return None


def write_image(path: str, img) -> None:
    cv2.imencode(".jpg", img, [cv2.IMWRITE_JPEG_QUALITY, 95])[1].tofile(path)


def _load_settings() -> dict:
    try:
        with open(SETTINGS_PATH, encoding="utf-8") as f:
            return json.load(f)
    except Exception:
        return {}


def _save_settings(**values) -> None:
    settings = _load_settings()
    settings.update(values)
    os.makedirs(MODELS_DIR, exist_ok=True)
    with open(SETTINGS_PATH, "w", encoding="utf-8") as f:
        json.dump(settings, f, indent=2)


def ensure_models(log=print) -> None:
    """Download the detector / recogniser the first time they're needed."""
    import requests
    os.makedirs(MODELS_DIR, exist_ok=True)
    for name, urls in MODEL_SOURCES.items():
        path = os.path.join(MODELS_DIR, name)
        if os.path.exists(path) and os.path.getsize(path) > 100_000:
            continue
        last_error = None
        for url in urls:
            try:
                log(f"[FACE] Downloading {name} ...")
                tmp = path + ".part"
                with requests.get(url, stream=True, timeout=30) as r:
                    r.raise_for_status()
                    with open(tmp, "wb") as f:
                        for chunk in r.iter_content(1 << 20):
                            f.write(chunk)
                if os.path.getsize(tmp) < 100_000:
                    raise ValueError("download too small (error page?)")
                os.replace(tmp, path)
                break
            except Exception as e:
                last_error = e
        else:
            raise RuntimeError(f"Could not download face model {name}: {last_error}")


# ─────────────────────────────── detection / embedding ───────────────────────────────

@dataclass
class Face:
    box: np.ndarray        # x, y, w, h in full-frame pixels
    landmarks: np.ndarray  # 5x2: two eyes, nose tip, two mouth corners
    score: float

    @property
    def area(self) -> float:
        return float(self.box[2] * self.box[3])

    @property
    def yaw(self) -> float:
        """Rough left/right head turn: 0 = facing the camera, +-0.5 = strongly turned."""
        eyes = self.landmarks[:2]
        eye_dist = max(float(np.linalg.norm(eyes[0] - eyes[1])), 1.0)
        return float((self.landmarks[2][0] - eyes[:, 0].mean()) / eye_dist)


class FaceEngine:
    """YuNet detection + ArcFace alignment/embedding. Safe to share between threads."""

    def __init__(self, threads: int = 2):
        ensure_models()
        import onnxruntime as ort
        self._lock = threading.Lock()
        self.detector = cv2.FaceDetectorYN.create(
            os.path.join(MODELS_DIR, YUNET_FILE), "", (320, 320), 0.7, 0.3, 50)
        opts = ort.SessionOptions()
        opts.intra_op_num_threads = threads   # leave CPU for the LLM
        opts.inter_op_num_threads = 1
        opts.log_severity_level = 3           # errors only
        self.session = ort.InferenceSession(os.path.join(MODELS_DIR, ARCFACE_FILE),
                                            sess_options=opts, providers=["CPUExecutionProvider"])
        self._input = self.session.get_inputs()[0].name

    def detect(self, img, max_side: int = 640) -> list[Face]:
        """Faces in the image, largest first."""
        h, w = img.shape[:2]
        scale = min(1.0, max_side / max(h, w))
        small = cv2.resize(img, (round(w * scale), round(h * scale)),
                           interpolation=cv2.INTER_AREA) if scale < 1.0 else img
        with self._lock:
            self.detector.setInputSize((small.shape[1], small.shape[0]))
            _, raw = self.detector.detect(small)
        faces = [] if raw is None else [
            Face(box=f[0:4] / scale, landmarks=f[4:14].reshape(5, 2) / scale, score=float(f[14]))
            for f in raw]
        faces.sort(key=lambda f: f.area, reverse=True)
        return faces

    @staticmethod
    def align(img, face: Face):
        """112x112 face in the standard ArcFace pose (landmarks from the full-res frame)."""
        lm = face.landmarks.astype(np.float32)
        eyes = lm[:2][np.argsort(lm[:2, 0])]
        mouth = lm[3:5][np.argsort(lm[3:5, 0])]
        src = np.vstack([eyes, lm[2:3], mouth])
        matrix, _ = cv2.estimateAffinePartial2D(src, ARCFACE_TEMPLATE, method=cv2.LMEDS)
        if matrix is None:
            raise ValueError("could not align face")
        return cv2.warpAffine(img, matrix, (112, 112), borderValue=0)

    def embed(self, crops: list) -> np.ndarray:
        """L2-normalised 512-d ArcFace embeddings for aligned 112x112 BGR crops."""
        if not crops:
            return np.zeros((0, 512), np.float32)
        out = []
        for crop in crops:   # the model's output is declared for batch size 1, so run one at a time
            blob = cv2.dnn.blobFromImage(crop, 1.0 / 127.5, (112, 112), (127.5, 127.5, 127.5), swapRB=True)
            with self._lock:
                out.append(self.session.run(None, {self._input: blob})[0][0])
        emb = np.asarray(out, dtype=np.float32)
        return emb / np.clip(np.linalg.norm(emb, axis=1, keepdims=True), 1e-9, None)

    @staticmethod
    def sharpness(crop) -> float:
        return float(cv2.Laplacian(cv2.cvtColor(crop, cv2.COLOR_BGR2GRAY), cv2.CV_64F).var())

    def quality_ok(self, face: Face, crop, strict: bool = False) -> tuple[bool, str]:
        """Reject faces that would give unreliable matches (strict = for enrollment)."""
        if face.score < (0.85 if strict else 0.75):
            return False, "face not clear"
        if min(face.box[2], face.box[3]) < (80 if strict else 48):
            return False, "too far from the camera"
        if abs(face.yaw) > (0.35 if strict else 0.5):
            return False, "head turned too far"
        if self.sharpness(crop) < (60 if strict else 25):
            return False, "blurry"
        if strict and not 45 <= float(crop.mean()) <= 215:
            return False, "lighting too dark or too bright"
        return True, ""


# ─────────────────────────────────── known faces ───────────────────────────────────

class FaceGallery:
    """Embeddings of every photo in known_faces/<Name>/, cached on disk."""

    def __init__(self, engine: FaceEngine, root: str):
        self.engine = engine
        self.root = root
        self._lock = threading.Lock()
        self.names: list[str] = []
        self.embeddings = np.zeros((0, 512), np.float32)
        self.skipped: list[str] = []
        self.multi_face: list[str] = []
        os.makedirs(root, exist_ok=True)
        self.reload()

    def _files(self):
        for person in sorted(os.listdir(self.root)):
            folder = os.path.join(self.root, person)
            if os.path.isdir(folder):
                for name in sorted(os.listdir(folder)):
                    if name.lower().endswith(IMAGE_EXTS):
                        yield person, os.path.join(folder, name)

    def _embed_file(self, path: str) -> dict:
        img = read_image(path)
        if img is None:
            return {"emb": None, "faces": 0}
        if os.path.basename(path).lower().startswith("aligned_") and img.shape[:2] == (112, 112):
            crop, n_faces = img, 1   # an enrollment sample, already aligned
        else:
            faces = self.engine.detect(img, max_side=960)
            if not faces:
                return {"emb": None, "faces": 0}
            crop, n_faces = self.engine.align(img, faces[0]), len(faces)  # the largest face
        return {"emb": self.engine.embed([crop])[0].round(6).tolist(), "faces": n_faces}

    def reload(self) -> dict:
        try:
            with open(GALLERY_CACHE, encoding="utf-8") as f:
                cache = json.load(f)
        except Exception:
            cache = {}
        names, embs, skipped, multi, new_cache = [], [], [], [], {}
        for person, path in self._files():
            mtime = os.path.getmtime(path)
            entry = cache.get(path)
            if not entry or abs(entry.get("mtime", 0) - mtime) > 1e-3:
                entry = {"mtime": mtime, **self._embed_file(path)}
            new_cache[path] = entry
            if entry["emb"] is None:
                skipped.append(path)
                continue
            if entry.get("faces", 1) > 1:
                multi.append(path)
            names.append(person)
            embs.append(np.asarray(entry["emb"], np.float32))
        with self._lock:
            self.names = names
            self.embeddings = np.vstack(embs) if embs else np.zeros((0, 512), np.float32)
            self.skipped, self.multi_face = skipped, multi
        try:
            os.makedirs(MODELS_DIR, exist_ok=True)
            with open(GALLERY_CACHE, "w", encoding="utf-8") as f:
                json.dump(new_cache, f)
        except Exception:
            pass
        return self.summary()

    def summary(self) -> dict:
        with self._lock:
            return {"people": dict(Counter(self.names)), "photos": len(self.names),
                    "skipped": [os.path.relpath(p, self.root) for p in self.skipped],
                    "multi_face": [os.path.relpath(p, self.root) for p in self.multi_face]}

    @property
    def people(self) -> list[str]:
        with self._lock:
            return sorted(set(self.names))

    def folder_for(self, name: str) -> str:
        """Existing folder with the same name (any case), else a new one."""
        for existing in os.listdir(self.root):
            if existing.lower() == name.lower() and os.path.isdir(os.path.join(self.root, existing)):
                return existing
        return name

    def match(self, emb) -> tuple[str | None, float, str | None, float]:
        """(best person, similarity, runner-up person, similarity)."""
        with self._lock:
            if not self.names:
                return None, 0.0, None, 0.0
            sims = self.embeddings @ emb
            names = self.names
        per_person: dict[str, list[float]] = {}
        for n, s in zip(names, sims):
            per_person.setdefault(n, []).append(float(s))
        scores = {}
        for n, s in per_person.items():
            s.sort(reverse=True)
            # With many samples, average the best two so one odd sample can't dominate.
            scores[n] = s[0] if len(s) < 4 else (s[0] + s[1]) / 2
        ranked = sorted(scores.items(), key=lambda kv: kv[1], reverse=True)
        n1, s1 = ranked[0]
        n2, s2 = ranked[1] if len(ranked) > 1 else (None, 0.0)
        return n1, s1, n2, s2

    def add_samples(self, name: str, crops: list) -> dict:
        folder = os.path.join(self.root, self.folder_for(name))
        os.makedirs(folder, exist_ok=True)
        stamp = time.strftime("%Y%m%d_%H%M%S")
        for i, crop in enumerate(crops):
            write_image(os.path.join(folder, f"aligned_{stamp}_{i:02d}.jpg"), crop)
        return self.reload()


# ─────────────────────────────── live camera + voting ───────────────────────────────

@dataclass
class _Track:
    box: np.ndarray
    last_seen: float
    face: Face | None = None
    last_recognised: float = 0.0
    last_best: tuple = (None, 0.0)
    votes: deque = field(default_factory=lambda: deque(maxlen=VOTE_WINDOW))

    def decision(self) -> tuple[str, str | None, float]:
        """('known', name, confidence) | ('unknown', None, confidence) | ('identifying', None, 0)."""
        if len(self.votes) < MIN_VOTES:
            return "identifying", None, 0.0
        named = [(n, s) for n, s in self.votes if n]
        if named:
            name, count = Counter(n for n, _ in named).most_common(1)[0]
            if count >= MIN_VOTES and count >= 0.6 * len(self.votes):
                sims = [s for n, s in named if n == name]
                conf = similarity_to_confidence(float(np.mean(sims))) * math.sqrt(count / len(self.votes))
                return "known", name, conf
        unknown = sum(1 for n, _ in self.votes if n is None)
        if unknown >= MIN_VOTES and unknown >= 0.6 * len(self.votes):
            return "unknown", None, unknown / len(self.votes)
        return "identifying", None, 0.0


def _iou(a, b) -> float:
    ax, ay, aw, ah = a
    bx, by, bw, bh = b
    ix = max(0.0, min(ax + aw, bx + bw) - max(ax, bx))
    iy = max(0.0, min(ay + ah, by + bh) - max(ay, by))
    inter = ix * iy
    union = aw * ah + bw * bh - inter
    return inter / union if union > 0 else 0.0


class FaceCamera:
    """Owns the webcam: detects, tracks and recognises faces on every frame and
    publishes an annotated preview + status for the desktop app."""

    def __init__(self, known_faces_dir: str, speak=None, log=print):
        self.engine = FaceEngine()
        self.gallery = FaceGallery(self.engine, known_faces_dir)
        self.speak = speak
        self.log = log
        self._state_lock = threading.Lock()   # start/stop
        self._lock = threading.Lock()         # published preview/status/tracks
        self._running = False
        self._cap = None
        self._cam_index = None
        self._thread = None
        self._tracks: list[_Track] = []
        self._reset_votes = False             # set by other threads, handled by the camera thread
        self._blocked = False                 # camera privacy mode (static placeholder frames)
        self._subscribers: list[queue.Queue] = []
        self._jpeg: bytes | None = None
        self._status: dict = {"running": False}
        preview_server().camera = self

    # ---- public API ----

    @property
    def running(self) -> bool:
        return self._running

    @property
    def blocked(self) -> bool:
        """True while the camera only delivers a privacy-mode placeholder image."""
        return self._running and self._blocked

    def start(self, greet: bool = True) -> str:
        with self._state_lock:
            if self._running:
                return "The camera is already on."
            cap, index, quality = self._open_camera()
            if cap is None:
                return ("I couldn't open a camera. Check that no other app is using it and that "
                        "camera access is allowed in Windows privacy settings.")
            self._blocked = quality == "blocked"
            self._cap, self._cam_index = cap, index
            self._tracks = []
            self._running = True
            with self._lock:
                self._status = {"running": True, "fps": 0.0, "camera": index,
                                "people_known": len(self.gallery.people), "faces": [], "primary": None}
            self._thread = threading.Thread(target=self._loop, daemon=True, name="xena-camera")
            self._thread.start()
        people = self.gallery.people
        msg = f"Camera on. I know {len(people)} {'person' if len(people) == 1 else 'people'}"
        msg += f": {', '.join(people)}." if people else "."
        if not people:
            msg += " Say 'remember my face as' and your name to teach me who you are."
        if quality == "blocked":
            msg += " " + BLOCKED_MESSAGE
        elif quality == "not_colour":
            msg += (" Warning: this camera gives no colour picture (an infrared camera, or a very "
                    "dark room), so recognition will be unreliable.")
        if greet and self.speak:
            threading.Thread(target=self._greet, daemon=True).start()
        return msg

    def stop(self) -> str:
        with self._state_lock:
            was_running = self._running
            self._running = False
            if self._thread and self._thread is not threading.current_thread():
                self._thread.join(timeout=3)
            if self._cap is not None:
                self._cap.release()
                self._cap = None
        with self._lock:
            self._tracks = []
            self._jpeg = None
            self._status = {"running": False}
        return "Camera off." if was_running else "The camera is already off."

    def latest_jpeg(self) -> bytes | None:
        with self._lock:
            return self._jpeg

    def status(self) -> dict:
        with self._lock:
            return dict(self._status)

    def primary(self) -> dict | None:
        """Decision for the largest face in view."""
        return self.status().get("primary")

    def identify(self, timeout: float = 4.0) -> dict | None:
        """Wait (up to timeout) for the main face to get a settled decision."""
        end, last = time.monotonic() + timeout, None
        while time.monotonic() < end and self._running:
            if self._blocked:
                return None   # privacy mode: nothing to see (callers check .blocked)
            p = self.primary()
            if p:
                last = p
                if p["state"] in ("known", "unknown"):
                    return p
            time.sleep(0.1)
        return last

    def face_crops(self, count: int = 4, timeout: float = 2.5, margin: float = 0.25) -> list:
        """Loose crops of the main face from the next few frames (for emotion analysis)."""
        q = self._subscribe()
        crops, end = [], time.monotonic() + timeout
        try:
            while len(crops) < count and time.monotonic() < end:
                try:
                    frame, faces = q.get(timeout=0.5)
                except queue.Empty:
                    continue
                if faces:
                    x, y, w, h = faces[0].box
                    mx, my = w * margin, h * margin
                    x0, y0 = int(max(0, x - mx)), int(max(0, y - my))
                    x1, y1 = int(min(frame.shape[1], x + w + mx)), int(min(frame.shape[0], y + h + my))
                    if x1 > x0 and y1 > y0:
                        crops.append(frame[y0:y1, x0:x1].copy())
                time.sleep(0.15)
        finally:
            self._unsubscribe(q)
        return crops

    def enroll(self, name: str, on_status=None, duration: float = 13.0,
               target: int = 16, minimum: int = 5) -> str:
        """Capture a varied set of good samples of the person in front of the camera."""
        name = re.sub(r"[^\w\- ]", "", name or "").strip()[:40]
        if not name:
            return "Tell me the name to save this face under, like: remember my face as Alex."
        name = name[0].upper() + name[1:]
        name = self.gallery.folder_for(name)

        def status(text):
            if on_status:
                try:
                    on_status(text)
                except Exception:
                    pass

        if not self._running:
            msg = self.start(greet=False)
            if not self._running:
                return msg
            time.sleep(1.0)

        phases = [(0.0, "Look straight at the camera and hold still"),
                  (3.5, "Now turn your head slightly to the left"),
                  (6.0, "Now slightly to the right"),
                  (8.5, "Now tilt your head slightly up, then down"),
                  (11.0, "Look at the camera again")]
        samples, embs = [], []
        rejected = Counter()
        q = self._subscribe()
        t0, phase, last_take, last_msg = time.monotonic(), -1, -1.0, ""
        try:
            while (now := time.monotonic() - t0) < duration and len(samples) < target:
                while phase + 1 < len(phases) and now >= phases[phase + 1][0]:
                    phase += 1
                msg = f"Learning {name}'s face: {phases[phase][1]}. ({len(samples)}/{target} samples)"
                if msg != last_msg:
                    status(msg)
                    last_msg = msg
                try:
                    frame, faces = q.get(timeout=0.5)
                except queue.Empty:
                    continue
                if now - last_take < 0.3:   # spread samples out in time
                    continue
                if not faces:
                    rejected["no face in view"] += 1
                    continue
                if len(faces) > 1 and faces[1].area > 0.45 * faces[0].area:
                    rejected["more than one person in view"] += 1
                    continue
                face = faces[0]
                try:
                    crop = self.engine.align(frame, face)
                except ValueError:
                    continue
                ok, why = self.engine.quality_ok(face, crop, strict=True)
                if not ok:
                    rejected[why] += 1
                    continue
                emb = self.engine.embed([crop])[0]
                if embs:
                    sims = [float(emb @ e) for e in embs]
                    if max(sims) > 0.97:   # near-identical frame adds nothing
                        rejected["same as a sample I already have"] += 1
                        continue
                    if min(sims) < 0.25:
                        rejected["looked like a different person"] += 1
                        continue
                samples.append(crop)
                embs.append(emb)
                last_take = now
        finally:
            self._unsubscribe(q)

        if len(samples) < minimum:
            reasons = ", ".join(f"{k} ({v})" for k, v in rejected.most_common(3)) or "no clear face"
            return (f"I only got {len(samples)} good samples, so I didn't save anything. "
                    f"Main problems: {reasons}. Try again facing the camera in good light.")

        centroid = np.mean(embs, axis=0)
        centroid /= np.linalg.norm(centroid)
        other, sim, _, _ = self.gallery.match(centroid)
        warning = ""
        if other and other.lower() != name.lower() and sim >= 0.45:
            warning = (f" Note: this face also closely matches {other} (similarity {sim:.2f}). "
                       f"If that's the same person, keep just one of the names.")
        self.gallery.add_samples(name, samples)
        self._reset_votes = True   # re-identify everyone with the updated gallery
        total = self.gallery.summary()["people"].get(name, len(samples))
        return f"Done. I've learned {name}'s face from {len(samples)} new samples ({total} in total).{warning}"

    def reload_gallery(self) -> dict:
        summary = self.gallery.reload()
        self._reset_votes = True
        return summary

    # ---- internals ----

    def _subscribe(self) -> queue.Queue:
        q = queue.Queue(maxsize=2)
        self._subscribers.append(q)
        return q

    def _unsubscribe(self, q) -> None:
        try:
            self._subscribers.remove(q)
        except ValueError:
            pass

    @staticmethod
    def _is_colour(frame) -> bool:
        small = cv2.resize(frame, (64, 36)).astype(np.int16)
        b, g, r = small[..., 0], small[..., 1], small[..., 2]
        return float(np.abs(b - g).mean() + np.abs(g - r).mean()) > 4.0 and float(small.mean()) > 15

    @staticmethod
    def _thumb(frame):
        return cv2.resize(frame, (32, 24), interpolation=cv2.INTER_AREA)

    @classmethod
    def _warm_up(cls, cap, timeout: float = 4.0):
        """Webcams send flat placeholder frames for a second or two after opening.
        Read until a real picture arrives (or time out). Returns (last frame, static):
        `static` means every frame was pixel-identical, which a live sensor never
        produces (there's always noise) - it's a privacy-mode placeholder image."""
        frame, prev, changed, count = None, None, False, 0
        end = time.monotonic() + timeout
        while time.monotonic() < end:
            ok, f = cap.read()
            if not ok or f is None:
                continue
            frame, count = f, count + 1
            thumb = cls._thumb(f)
            if prev is not None and not np.array_equal(thumb, prev):
                changed = True
            prev = thumb
            if changed and float(cv2.cvtColor(thumb, cv2.COLOR_BGR2GRAY).std()) > 8.0:
                break   # a live, real scene
        return frame, (count >= 10 and not changed)

    @staticmethod
    def _open_capture(index: int):
        # Media Foundation is much faster than DirectShow on modern Windows
        # webcams (measured: ~29 fps vs ~4 fps); DirectShow is the fallback.
        for api in (cv2.CAP_MSMF, cv2.CAP_DSHOW):
            cap = cv2.VideoCapture(index, api)
            if cap.isOpened():
                # 640x480 keeps the frame rate high; faces are still well over the
                # size the recogniser needs.
                cap.set(cv2.CAP_PROP_FRAME_WIDTH, 640)
                cap.set(cv2.CAP_PROP_FRAME_HEIGHT, 480)
                return cap
            cap.release()
        return None

    def _open_camera(self):
        """Open the first camera giving a live colour picture (laptops may also
        expose an infrared Windows Hello camera). Remembers the choice.
        Returns (capture, index, quality) with quality "ok" | "blocked" | "not_colour"."""
        preferred = _load_settings().get("camera_index")
        order = ([preferred] if isinstance(preferred, int) else []) + \
                [i for i in range(3) if i != preferred]
        fallback = None
        for index in order:
            cap = self._open_capture(index)
            if cap is None:
                continue
            frame, static = self._warm_up(cap)
            if frame is None:
                cap.release()
                continue
            if not static and self._is_colour(frame):
                if fallback:
                    fallback[0].release()
                _save_settings(camera_index=index)
                return cap, index, "ok"
            quality = "blocked" if static else "not_colour"
            if fallback is None:
                fallback = (cap, index, quality)
            else:
                cap.release()
        if fallback:
            return fallback
        return None, None, "none"

    def _greet(self) -> None:
        who = self.identify(timeout=6.0)
        if who and who.get("state") == "known" and self.speak:
            self.speak(f"Hello, {who['name']}.")

    def _loop(self) -> None:
        stamps = deque(maxlen=30)
        failures = 0
        prev_thumb, static_run = None, 0
        while self._running:
            ok, frame = self._cap.read() if self._cap is not None else (False, None)
            if not ok or frame is None:
                failures += 1
                if failures > 100:
                    self.log("[FACE] Camera stopped delivering frames.")
                    if self._cap is not None:
                        self._cap.release()
                        self._cap = None
                    break
                time.sleep(0.02)
                continue
            failures = 0
            now = time.monotonic()
            stamps.append(now)

            # Privacy mode shows a frozen placeholder: pixel-identical frames, which a
            # live sensor never produces. Re-checked constantly (the key can be toggled).
            thumb = self._thumb(frame)
            static_run = static_run + 1 if prev_thumb is not None and np.array_equal(thumb, prev_thumb) else 0
            prev_thumb = thumb
            self._blocked = static_run >= 30
            try:
                faces = [] if self._blocked else self.engine.detect(frame)
                self._update_tracks(frame, faces, now)
                for q in list(self._subscribers):
                    try:
                        q.put_nowait((frame, faces))
                    except queue.Full:
                        pass
                self._publish(frame, stamps)
            except Exception as e:
                self.log(f"[FACE] frame error: {e}")
        self._running = False
        with self._lock:
            self._status = {"running": False}
            self._jpeg = None

    def _update_tracks(self, frame, faces: list[Face], now: float) -> None:
        tracks = list(self._tracks)
        if self._reset_votes:
            self._reset_votes = False
            for t in tracks:
                t.votes.clear()
        free = list(range(len(tracks)))
        seen = []
        for face in faces:
            best, best_iou = None, 0.3
            for i in free:
                iou = _iou(tracks[i].box, face.box)
                if iou > best_iou:
                    best, best_iou = i, iou
            if best is None:
                track = _Track(box=face.box.copy(), last_seen=now)
                tracks.append(track)
            else:
                track = tracks[best]
                free.remove(best)
                track.box = 0.6 * face.box + 0.4 * track.box
                track.last_seen = now
            track.face = face
            seen.append(track)

        # Recognise the tracks that are due, in one batch.
        due, crops = [], []
        for track in seen:
            if now - track.last_recognised < RECOGNISE_EVERY:
                continue
            track.last_recognised = now
            try:
                crop = self.engine.align(frame, track.face)
            except ValueError:
                continue
            if self.engine.quality_ok(track.face, crop)[0]:
                due.append(track)
                crops.append(crop)
        if crops:
            for track, emb in zip(due, self.engine.embed(crops)):
                n1, s1, _, s2 = self.gallery.match(emb)
                accepted = n1 is not None and s1 >= MATCH_THRESHOLD and (s1 - s2) >= MIN_MARGIN
                track.votes.append((n1 if accepted else None, s1))
                track.last_best = (n1, s1)

        with self._lock:
            self._tracks = [t for t in tracks if now - t.last_seen < 1.0]

    def _publish(self, frame, stamps) -> None:
        h, w = frame.shape[:2]
        scale = PREVIEW_WIDTH / w
        preview = cv2.flip(cv2.resize(frame, (PREVIEW_WIDTH, round(h * scale)),
                                      interpolation=cv2.INTER_AREA), 1)   # mirror, like a selfie view
        faces_out = []
        with self._lock:
            tracks = sorted(self._tracks, key=lambda t: t.box[2] * t.box[3], reverse=True)
        for t in tracks:
            state, name, conf = t.decision()
            x, y, bw, bh = (t.box * scale).astype(int)
            x = PREVIEW_WIDTH - (x + bw)   # mirrored
            colour = CYAN if state == "known" else ORANGE if state == "unknown" else GREY
            label = (f"{name} {conf * 100:.0f}%" if state == "known"
                     else "Unknown" if state == "unknown" else "Identifying...")
            self._draw_box(preview, x, y, bw, bh, colour, label)
            best_name, best_sim = t.last_best
            faces_out.append({"state": state, "name": name, "confidence": round(conf, 3),
                              "closest": best_name, "closest_similarity": round(float(best_sim), 3)})
        ok, jpg = cv2.imencode(".jpg", preview, [cv2.IMWRITE_JPEG_QUALITY, 72])
        fps = (len(stamps) - 1) / (stamps[-1] - stamps[0]) if len(stamps) > 1 and stamps[-1] > stamps[0] else 0.0
        with self._lock:
            if ok:
                self._jpeg = jpg.tobytes()
            self._status = {"running": True, "fps": round(fps, 1), "camera": self._cam_index,
                            "blocked": self._blocked,
                            "people_known": len(self.gallery.people), "faces": faces_out,
                            "primary": faces_out[0] if faces_out else None}

    @staticmethod
    def _draw_box(img, x, y, w, h, colour, label) -> None:
        corner = max(8, min(w, h) // 5)
        for (px, py, dx, dy) in ((x, y, 1, 1), (x + w, y, -1, 1), (x, y + h, 1, -1), (x + w, y + h, -1, -1)):
            cv2.line(img, (px, py), (px + dx * corner, py), colour, 2, cv2.LINE_AA)
            cv2.line(img, (px, py), (px, py + dy * corner), colour, 2, cv2.LINE_AA)
        cv2.rectangle(img, (x, y), (x + w, y + h), colour, 1, cv2.LINE_AA)
        (tw, th), _ = cv2.getTextSize(label, cv2.FONT_HERSHEY_SIMPLEX, 0.5, 1)
        ty = y - 6 if y - th - 10 > 0 else y + h + th + 8
        cv2.rectangle(img, (x, ty - th - 5), (x + tw + 8, ty + 4), colour, -1)
        cv2.putText(img, label, (x + 4, ty), cv2.FONT_HERSHEY_SIMPLEX, 0.5, (10, 10, 10), 1, cv2.LINE_AA)


# ─────────────────────────── preview server for the desktop app ───────────────────────────

class _PreviewServer:
    """Tiny local HTTP server the desktop app polls for the live preview. It runs
    separately from the command channel, so the video never waits on the LLM."""

    def __init__(self):
        self.camera: FaceCamera | None = None
        owner = self

        class Handler(BaseHTTPRequestHandler):
            # Keep-alive: the app reuses one connection instead of opening a new
            # socket for every frame (~15 per second while the camera runs).
            protocol_version = "HTTP/1.1"

            def do_GET(self):
                cam = owner.camera
                if self.path.startswith("/frame"):
                    data = cam.latest_jpeg() if cam else None
                    if not data:
                        self._empty(204)
                        return
                    self._send(data, "image/jpeg")
                elif self.path.startswith("/status"):
                    status = cam.status() if cam else {"running": False}
                    self._send(json.dumps(status).encode("utf-8"), "application/json")
                else:
                    self._empty(404)

            def _empty(self, code: int):
                self.send_response(code)
                self.send_header("Content-Length", "0")
                self.end_headers()

            def _send(self, body: bytes, content_type: str):
                self.send_response(200)
                self.send_header("Content-Type", content_type)
                self.send_header("Cache-Control", "no-store")
                self.send_header("Content-Length", str(len(body)))
                self.end_headers()
                self.wfile.write(body)

            def log_message(self, *args):
                pass

        self.httpd = ThreadingHTTPServer(("127.0.0.1", 0), Handler)
        self.httpd.daemon_threads = True
        threading.Thread(target=self.httpd.serve_forever, daemon=True, name="xena-preview").start()

    @property
    def port(self) -> int:
        return self.httpd.server_address[1]


_preview_server: _PreviewServer | None = None
_preview_lock = threading.Lock()


def preview_server() -> _PreviewServer:
    """The shared preview server (cheap: starting it doesn't load any models)."""
    global _preview_server
    with _preview_lock:
        if _preview_server is None:
            _preview_server = _PreviewServer()
        return _preview_server
