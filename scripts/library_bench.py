"""Сравнение локальных моделей для библиотекаря: одна и та же работа (подробно задокументировать фрагмент сессии),
замер скорости, объёма и аккуратности. Поднимает собственный Ollama на порту 11436 (модели читает из общей папки).

    python scripts/library_bench.py data/library/bench/session.txt qwen3:8b gemma4:e2b-it-qat ...
"""
import json, os, subprocess, sys, time, urllib.request

PORT = 11436
SYSTEM = open(os.path.join(os.path.dirname(__file__), "library_prompt.txt"), encoding="utf-8").read()


def call(path, payload, timeout=1200):
    req = urllib.request.Request(f"http://127.0.0.1:{PORT}{path}", json.dumps(payload).encode(), {"Content-Type": "application/json"})
    with urllib.request.urlopen(req, timeout=timeout) as r:
        return json.load(r)


def main():
    source, models = sys.argv[1], sys.argv[2:]
    text = open(source, encoding="utf-8").read()
    env = dict(os.environ, OLLAMA_HOST=f"127.0.0.1:{PORT}", OLLAMA_MAX_LOADED_MODELS="1", OLLAMA_NUM_PARALLEL="1")
    server = subprocess.Popen(["ollama", "serve"], env=env, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    out_dir = os.path.join(os.path.dirname(source), "results")
    os.makedirs(out_dir, exist_ok=True)
    try:
        for _ in range(60):
            try:
                call("/api/tags", None, 2) if False else urllib.request.urlopen(f"http://127.0.0.1:{PORT}/api/tags", timeout=2)
                break
            except Exception:
                time.sleep(1)
        for model in models:
            t = time.time()
            try:
                r = call("/api/chat", {
                    "model": model, "stream": False, "think": False,
                    "messages": [{"role": "system", "content": SYSTEM}, {"role": "user", "content": text}],
                    "options": {"temperature": 0.2, "num_ctx": 12288, "num_predict": 1800},
                })
            except Exception as e:  # noqa: BLE001
                print(f"{model}: FAILED {e!r}"[:200], flush=True)
                continue
            dt = time.time() - t
            answer = r["message"]["content"]
            tps = r.get("eval_count", 0) / max(1e-9, r.get("eval_duration", 1) / 1e9)
            name = model.replace(":", "_").replace("/", "_")
            open(os.path.join(out_dir, f"{os.path.basename(source)}.{name}.md"), "w", encoding="utf-8").write(answer)
            print(f"{model}: {dt:.0f} s total, {r.get('eval_count', 0)} tokens at {tps:.1f} tok/s, answer {len(answer)} chars", flush=True)
            call("/api/generate", {"model": model, "keep_alive": 0})
    finally:
        server.terminate()


main()
