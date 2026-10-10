"""Проба Qwen3-TTS VoiceDesign на GPU: русская речь с описанием интонации, замер скорости."""
import os, sys, time
os.environ.setdefault("HF_HOME", r"C:\EDITH\data\cache\hf")
import torch, soundfile as sf
from qwen_tts import Qwen3TTSModel

OUT = r"C:\EDITH\data\qwen-test"
VOICE = ("Молодая русская женщина, около 30 лет, тёплый низкий голос, говорит естественно и живо, "
         "как умный близкий друг: с лёгкими паузами, смешками, меняет темп и интонацию.")
PHRASES = [
    ("plain", "Сейчас в Самаре плюс четырнадцать, облачно. К вечеру будет прохладнее, возьмите куртку.", ""),
    ("warm", "Хм, дайте-ка подумать... Нет, вчера вы про это не говорили. Но я могу поискать в заметках.", "Задумчиво, с паузами."),
    ("laugh", "Ха-ха, ну вы даёте! Опять забыли, куда положили ключи? Ладно, давайте вместе вспомним.", "Весело, с лёгким смехом."),
    ("sad", "К сожалению, сегодня не получилось. Но ничего страшного, завтра попробуем ещё раз.", "Мягко, с сочувствием."),
    ("long", "Слушайте, у меня тут одна мысль появилась. Вы же в среду хотели доделать отчёт? Так вот, "
             "если начать прямо сейчас, к вечеру спокойно успеете, и ещё останется время отдохнуть.", "Бодро, заинтересованно."),
]

t = time.time()
model = Qwen3TTSModel.from_pretrained("Qwen/Qwen3-TTS-12Hz-1.7B-VoiceDesign", device_map="cuda:0",
                                      dtype=torch.float16, attn_implementation="sdpa")
print(f"load {time.time()-t:.1f}s  vram {torch.cuda.memory_allocated()/2**30:.2f} GB", flush=True)
print("languages:", model.get_supported_languages() if hasattr(model, "get_supported_languages") else "?")
for name, text, mood in PHRASES:
    t = time.time()
    wavs, sr = model.generate_voice_design(text=text, language="Russian", instruct=(VOICE + " " + mood).strip())
    dt = time.time() - t
    dur = len(wavs[0]) / sr
    sf.write(os.path.join(OUT, f"qwen_{name}.wav"), wavs[0], sr)
    print(f"{name}: audio {dur:.1f}s in {dt:.1f}s (RTF {dt/dur:.2f}) peak vram {torch.cuda.max_memory_allocated()/2**30:.2f} GB", flush=True)
