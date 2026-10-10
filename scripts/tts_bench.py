"""Compare TTS engines on EDITH's real phrases. Writes models/tts-bench/out/<engine>/<n>.wav.

Usage: python tts_bench.py silero|espeech|qwen3
Aborts when something other than EDITH's own brain model occupies the GPU.
"""
import os, subprocess, sys, time

OUT = r"C:\EDITH\models\tts-bench\out"
REF_AUDIO = r"C:\EDITH\models\tts-bench\example.mp3"
REF_TEXT = ("Разработчики много времени уделили физике этого явления, чтобы всё было как в реальном мире, "
            "как в настоящем хоккейном матче. Поэтому даже записали этот процесс в мокапе.")

PHRASES = [
    "На диске Си свободно четыреста тридцать девять гигабайт, на диске Ди — двадцать пять.",
    "Я помню чудное мгновенье: передо мной явилась ты, как мимолётное виденье, как гений чистой красоты.",
    "Открываю мессенджер ВКонтакте.",
    "Понимаю, после долгого дня в танках правда хочется отдохнуть. Может, включить музыку?",
    "Напоминаю: пора проверить чайник. Сделано?",
]


def gpu_guard():
    used = int(subprocess.run(["nvidia-smi", "--query-gpu=memory.used", "--format=csv,noheader,nounits"],
                              capture_output=True, text=True).stdout.strip())
    if used > 7200:
        raise SystemExit(f"ABORT: GPU busy ({used} MB)")


def save(engine, i, audio, sr):
    import wave
    import numpy as np
    samples = np.asarray(audio, dtype=np.float32).reshape(-1)
    os.makedirs(os.path.join(OUT, engine), exist_ok=True)
    with wave.open(os.path.join(OUT, engine, f"{i}.wav"), "wb") as w:
        w.setnchannels(1)
        w.setsampwidth(2)
        w.setframerate(int(sr))
        w.writeframes((np.clip(samples, -1, 1) * 32767).astype("<i2").tobytes())
    return len(samples) / sr


def run(engine, synth):
    for i, text in enumerate(PHRASES, 1):
        gpu_guard()
        t = time.time()
        audio, sr = synth(text)
        took = time.time() - t
        dur = save(engine, i, audio, sr)
        print(f"{engine} #{i}: synth {took:5.2f}s for {dur:4.1f}s audio (RTF {took / max(dur, 0.01):.2f})", flush=True)


def silero():
    import torch
    model = torch.package.PackageImporter(r"C:\EDITH\models\tts\silero\v5_ru.pt").load_pickle("tts_models", "model")
    torch.set_num_threads(4)
    run("silero", lambda text: (model.apply_tts(text=text, speaker="xenia", sample_rate=48000).numpy(), 48000))


def patch_torchaudio_load():
    """torchaudio >= 2.9 loads audio through torchcodec, which needs FFmpeg DLLs; soundfile does not."""
    import soundfile as sf
    import torch
    import torchaudio

    def load(path, *args, **kwargs):
        data, sr = sf.read(str(path), dtype="float32", always_2d=True)
        return torch.from_numpy(data.T.copy()), sr

    torchaudio.load = load


def espeech():
    patch_torchaudio_load()
    import torch
    from huggingface_hub import hf_hub_download
    from f5_tts.infer.utils_infer import infer_process, load_model, load_vocoder, preprocess_ref_audio_text
    from f5_tts.model import DiT
    from ruaccent import RUAccent

    model_file = hf_hub_download("ESpeech/ESpeech-TTS-1_RL-V2", "espeech_tts_rlv2.pt")
    vocab_file = hf_hub_download("ESpeech/ESpeech-TTS-1_RL-V2", "vocab.txt")
    cfg = dict(dim=1024, depth=22, heads=16, ff_mult=2, text_dim=512, conv_layers=4)
    model = load_model(DiT, cfg, model_file, vocab_file=vocab_file)
    vocoder = load_vocoder()
    if os.environ.get("ACCENT", "1") == "1":
        accent = RUAccent()
        accent.load(omograph_model_size="turbo3.1", use_dictionary=True, tiny_mode=False)
        stress = accent.process_all
    else:
        stress = lambda text: text  # noqa: E731
    ref_audio, ref_text = preprocess_ref_audio_text(REF_AUDIO, stress(REF_TEXT))
    nfe = int(os.environ.get("NFE", "16"))

    def synth(text):
        wave, sr, _ = infer_process(ref_audio, ref_text, stress(text), model, vocoder, nfe_step=nfe, speed=1.0)
        return wave, sr

    run(f"espeech-nfe{nfe}" + ("" if os.environ.get("ACCENT", "1") == "1" else "-noaccent"), synth)


def qwen3():
    import torch
    from qwen_tts import Qwen3TTSModel

    dtype = torch.float16 if os.environ.get("DTYPE", "fp16") == "fp16" else torch.float32
    model = Qwen3TTSModel.from_pretrained("Qwen/Qwen3-TTS-12Hz-0.6B-Base", device_map="cuda:0", dtype=dtype, attn_implementation="sdpa")

    def synth(text):
        wavs, sr = model.generate_voice_clone(text=text, language="Russian", ref_audio=REF_AUDIO, ref_text=REF_TEXT)
        return wavs[0], sr

    run("qwen3-0.6b", synth)


if __name__ == "__main__":
    {"silero": silero, "espeech": espeech, "qwen3": qwen3}[sys.argv[1]]()
