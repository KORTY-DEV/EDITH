"""Persistent local Silero v5 Russian worker for EDITH.

Protocol: one JSON object per stdin line, one JSON object per stdout line.
The model is loaded once; audio is written to the caller-provided WAV path.
"""

from __future__ import annotations

import argparse
import json
import os
import sys
import threading
import time
import wave

import numpy as np
import torch


VOICES = {"xenia", "kseniya", "baya", "aidar", "eugene"}


def emit(payload: dict) -> None:
    sys.stdout.write(json.dumps(payload, ensure_ascii=False, separators=(",", ":")) + "\n")
    sys.stdout.flush()


def write_wav(audio: torch.Tensor, path: str, sample_rate: int, speed: float) -> int:
    values = audio.detach().cpu().numpy().astype(np.float32, copy=False).reshape(-1)
    values = np.clip(values, -1.0, 1.0)
    # Silero v5 exposes speed through duration controls in its newer API, while
    # the v5 package used here does not. A rate-adjusted WAV preserves the
    # requested duration control without another DSP dependency or GPU work.
    playback_rate = max(8000, int(round(sample_rate * max(0.5, min(2.0, speed)))))
    pcm = (values * 32767.0).astype("<i2", copy=False).tobytes()
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with wave.open(path, "wb") as output:
        output.setnchannels(1)
        output.setsampwidth(2)
        output.setframerate(playback_rate)
        output.writeframes(pcm)
    return int(values.shape[0])


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--model", required=True)
    parser.add_argument("--threads", type=int, default=4)
    parser.add_argument("--accent-dir", default="", help="RUAccent models folder; empty = no automatic stress")
    parser.add_argument("--accent-model", default="tiny2.1")
    args = parser.parse_args()

    torch.set_num_threads(max(1, args.threads))
    started = time.perf_counter()
    model = torch.package.PackageImporter(args.model).load_pickle("tts_models", "model")
    model.to(torch.device("cpu"))
    # RUAccent takes ~10 s to load: do it in the background so the voice is ready as soon as
    # Silero is; until the stress model arrives, text goes out with Silero's own stress.
    state = {"accentizer": None}
    accent_dir = args.accent_dir if args.accent_dir and os.path.isdir(args.accent_dir) else ""

    def load_accent() -> None:
        loaded = time.perf_counter()
        try:
            sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
            from accent import Accentizer

            state["accentizer"] = Accentizer(accent_dir, args.accent_model)
            sys.stderr.write(f"accent ready in {time.perf_counter() - loaded:.1f}s\n")
        except Exception as exc:  # no stress model: Silero's own stress still works
            sys.stderr.write(f"accent unavailable: {exc}\n")
        sys.stderr.flush()

    if accent_dir:
        threading.Thread(target=load_accent, name="accent-loader", daemon=True).start()
    emit({"event": "ready", "sample_rate": 48000, "voices": sorted(VOICES), "accent": bool(accent_dir),
          "accent_error": "", "load_ms": round((time.perf_counter() - started) * 1000.0, 1)})

    for raw in sys.stdin:
        if not raw.strip():
            continue
        try:
            request = json.loads(raw)
            operation = request.get("op")
            if operation == "health":
                emit({"event": "health", "ok": True})
                continue
            if operation == "shutdown":
                emit({"event": "shutdown"})
                return 0
            if operation != "synthesize":
                emit({"event": "error", "error": "unknown-operation"})
                continue

            voice = str(request.get("voice", "xenia")).lower()
            if voice not in VOICES:
                raise ValueError(f"unsupported voice: {voice}")
            text = str(request.get("text", "")).strip()
            output = str(request["output"])
            sample_rate = int(request.get("sample_rate", 48000))
            speed = float(request.get("speed", 1.0))
            if not text:
                raise ValueError("empty text")

            started = time.perf_counter()
            if state["accentizer"] is not None:
                text = state["accentizer"].apply(text)
            kwargs = {"speaker": voice, "sample_rate": sample_rate}
            if text.startswith("<speak>"):
                kwargs["ssml_text"] = text
            else:
                kwargs["text"] = text
            audio = model.apply_tts(**kwargs)
            samples = write_wav(audio, output, sample_rate, speed)
            emit({"event": "done", "id": request.get("id"), "output": output, "samples": samples, "sample_rate": sample_rate, "synthesis_ms": round((time.perf_counter() - started) * 1000.0, 1)})
        except Exception as exc:  # keep the persistent worker alive for the next request
            emit({"event": "error", "id": request.get("id") if isinstance(request, dict) else None, "error": str(exc)})

    return 0


if __name__ == "__main__":
    raise SystemExit(main())
