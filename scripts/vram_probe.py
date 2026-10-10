"""Сколько памяти и процессора ест локальная модель библиотекаря: видеопамять, оперативная память, нагрузка, скорость."""
import json, os, subprocess, sys, time, urllib.request

PORT = 11436


def call(path, payload=None, timeout=900):
    data = json.dumps(payload).encode() if payload is not None else None
    req = urllib.request.Request(f"http://127.0.0.1:{PORT}{path}", data, {"Content-Type": "application/json"})
    with urllib.request.urlopen(req, timeout=timeout) as r:
        return json.load(r)


def smi():
    out = subprocess.run(["nvidia-smi", "--query-gpu=memory.used,utilization.gpu", "--format=csv,noheader,nounits"], capture_output=True, text=True).stdout
    used, util = [int(x) for x in out.strip().split(",")]
    return used, util


def ollama_ram_mb():
    out = subprocess.run(["powershell", "-NoProfile", "-Command",
                          "(Get-Process ollama*, 'ollama_llama_server' -ErrorAction SilentlyContinue | Measure-Object WorkingSet64 -Sum).Sum / 1MB"],
                         capture_output=True, text=True).stdout.strip()
    return float(out.replace(",", ".") or 0)


text = open(sys.argv[1], encoding="utf-8").read()
base_used, _ = smi()
env = dict(os.environ, OLLAMA_HOST=f"127.0.0.1:{PORT}", OLLAMA_MAX_LOADED_MODELS="1", OLLAMA_NUM_PARALLEL="1")
server = subprocess.Popen(["ollama", "serve"], env=env, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
try:
    for _ in range(60):
        try:
            urllib.request.urlopen(f"http://127.0.0.1:{PORT}/api/tags", timeout=2)
            break
        except Exception:
            time.sleep(1)
    print(f"фон: видеопамять занята {base_used} МБ без модели")
    for model in sys.argv[2:]:
        t = time.time()
        r = call("/api/chat", {"model": model, "stream": False, "think": False,
                               "messages": [{"role": "user", "content": text[:int(os.environ.get("CHARS", "9000"))] + "\n\nСделай подробную запись по этому тексту."}],
                               "options": dict({"temperature": 0.2, "num_ctx": int(os.environ.get("CTX", "12288")), "num_predict": 400}, **({"num_gpu": int(os.environ["NGPU"])} if "NGPU" in os.environ else {}))})
        peak_used, util = smi()
        ps = call("/api/ps")["models"][0]
        tok_s = r["eval_count"] / (r["eval_duration"] / 1e9)
        print(f"{model}: модель {ps['size'] / 2**30:.1f} ГБ всего, из них в видеопамяти {ps['size_vram'] / 2**30:.1f} ГБ "
              f"({100 * ps['size_vram'] / ps['size']:.0f}%); видеокарта занята {peak_used} МБ (+{peak_used - base_used}); "
              f"GPU {util}%; оперативная память Ollama {ollama_ram_mb():.0f} МБ; {tok_s:.0f} ток/с; {time.time() - t:.0f} с")
        call("/api/generate", {"model": model, "keep_alive": 0})
        time.sleep(3)
finally:
    server.terminate()
