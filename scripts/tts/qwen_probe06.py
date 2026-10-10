"""Проба Qwen3-TTS 0.6B (CustomVoice и клонирование голоса) на GPU: русский, замер скорости."""
import os, time
os.environ.setdefault("HF_HOME", r"C:\EDITH\data\cache\hf")
import torch, soundfile as sf
from qwen_tts import Qwen3TTSModel

DT = getattr(torch, os.environ.get("QDT", "float16"))
OUT = r"C:\EDITH\data\qwen-test"
TEXT = "Хм, дайте-ка подумать. Нет, вчера вы про это не говорили. Но я могу поискать в заметках."

def run(tag, loader, call):
    t = time.time()
    model = loader()
    print(f"[{tag}] load {time.time()-t:.1f}s vram {torch.cuda.memory_allocated()/2**30:.2f} GB", flush=True)
    for i in range(2):
        t = time.time()
        wavs, sr = call(model)
        dt = time.time() - t
        dur = len(wavs[0]) / sr
        print(f"[{tag}] run{i}: audio {dur:.1f}s in {dt:.1f}s RTF {dt/dur:.2f}", flush=True)
    sf.write(os.path.join(OUT, f"qwen06_{tag}.wav"), wavs[0], sr)
    del model; torch.cuda.empty_cache()

try:
    run("custom", lambda: Qwen3TTSModel.from_pretrained("Qwen/Qwen3-TTS-12Hz-0.6B-CustomVoice", device_map="cuda:0", dtype=DT, attn_implementation="sdpa"),
        lambda m: m.generate_custom_voice(text=TEXT, language="Russian", speaker="Serena"))
except Exception as e:
    print("custom failed:", repr(e)[:400])

try:
    ref = os.path.join(OUT, "qwen_plain.wav")
    def clone(m):
        prompt = m.create_voice_clone_prompt(ref_audio=ref, ref_text="Сейчас в Самаре плюс четырнадцать, облачно. К вечеру будет прохладнее, возьмите куртку.")
        return m.generate_voice_clone(text=TEXT, language="Russian", voice_clone_prompt=prompt)
    run("clone", lambda: Qwen3TTSModel.from_pretrained("Qwen/Qwen3-TTS-12Hz-0.6B-Base", device_map="cuda:0", dtype=DT, attn_implementation="sdpa"), clone)
except Exception as e:
    print("clone failed:", repr(e)[:400])
