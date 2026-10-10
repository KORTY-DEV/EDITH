"""EDITH brain benchmark. Aborts as soon as something else uses the GPU."""
import json, subprocess, sys, time, urllib.request

OLLAMA = "http://127.0.0.1:11434"
MODELS = sys.argv[1:] or ["qwen3.5:4b", "qwen3.5:9b", "qwen3:8b", "lfm2.5:8b", "ministral-3:14b", "gpt-oss:20b", "qwen3.6:35b"]

SYSTEM = ("Ты E.D.I.T.H. — спокойный, тёплый женский голосовой ассистент Windows, личный дворецкий. "
          "Отвечай по-русски, коротко (1-3 предложения), без markdown. Говори о себе в женском роде. "
          "Данные о состоянии ПК получай только инструментами, не выдумывай.")

TOOLS = [
    {"type": "function", "function": {"name": "system_snapshot", "description": "Оперативная память, диски, версия Windows.", "parameters": {"type": "object", "properties": {}, "required": []}}},
    {"type": "function", "function": {"name": "processes", "description": "Процессы с загрузкой CPU и памятью.", "parameters": {"type": "object", "properties": {}, "required": []}}},
    {"type": "function", "function": {"name": "find_files", "description": "Найти файлы по маске в папке.", "parameters": {"type": "object", "properties": {"root": {"type": "string"}, "pattern": {"type": "string"}}, "required": ["root", "pattern"]}}},
    {"type": "function", "function": {"name": "search_web", "description": "Поиск свежих фактов в интернете.", "parameters": {"type": "object", "properties": {"query": {"type": "string"}}, "required": ["query"]}}},
    {"type": "function", "function": {"name": "open_app", "description": "Открыть приложение, только по явной просьбе.", "parameters": {"type": "object", "properties": {"name": {"type": "string"}}, "required": ["name"]}}},
]

CASES = [
    ("tool", "сколько свободно места на дисках?", "system_snapshot"),
    ("tool", "что сейчас больше всего грузит компьютер?", "processes"),
    ("tool", "найди в загрузках все pdf-файлы", "find_files"),
    ("tool", "какой сейчас курс доллара?", "search_web"),
    ("notool", "привет, как настроение?", None),
    ("notool", "что такое SSD, коротко?", None),
    ("chat", "расскажи четверостишие Пушкина", None),
    ("chat", "я сегодня весь день играл в танки и устал", None),
]


def http(path, body=None, timeout=300):
    req = urllib.request.Request(OLLAMA + path, data=None if body is None else json.dumps(body).encode(),
                                 headers={"Content-Type": "application/json"})
    with urllib.request.urlopen(req, timeout=timeout) as r:
        return json.loads(r.read())


def gpu():
    out = subprocess.run(["nvidia-smi", "--query-gpu=utilization.gpu,memory.used", "--format=csv,noheader,nounits"],
                         capture_output=True, text=True).stdout.strip().split(",")
    return int(out[0]), int(out[1])


def guard(own):
    """Foreign load = another Ollama model loaded, or VRAM beyond what our model explains."""
    loaded = http("/api/ps").get("models", [])
    foreign = [m["name"] for m in loaded if m["name"] != own]
    util, used = gpu()
    own_mb = sum(m.get("size_vram", 0) for m in loaded if m["name"] == own) // (1024 * 1024)
    if foreign or used - own_mb > 2200:
        raise SystemExit(f"ABORT: GPU busy by someone else (foreign={foreign}, used={used}MB, ours={own_mb}MB, util={util}%)")


def chat(model, prompt, tools):
    body = {"model": model, "stream": False, "think": False, "keep_alive": "5m",
            "messages": [{"role": "system", "content": SYSTEM}, {"role": "user", "content": prompt}],
            "options": {"temperature": 0.2, "num_ctx": 4096, "num_predict": 200}}
    if tools:
        body["tools"] = TOOLS
    t = time.time()
    try:
        r = http("/api/chat", body)
    except Exception as e:  # noqa
        return {"error": str(e)[:200], "wall": time.time() - t}
    msg = r.get("message", {})
    calls = [c["function"]["name"] for c in msg.get("tool_calls", []) or []]
    gen_ms = r.get("eval_duration", 0) / 1e6
    return {"wall": time.time() - t, "load_ms": r.get("load_duration", 0) / 1e6,
            "tps": r.get("eval_count", 0) / (gen_ms / 1000) if gen_ms else 0,
            "prompt_ms": r.get("prompt_eval_duration", 0) / 1e6,
            "calls": calls, "text": (msg.get("content") or "").strip()}


def main():
    results = {}
    for model in MODELS:
        guard(None)
        print(f"\n=== {model}", flush=True)
        rows = []
        for kind, prompt, expected in CASES:
            guard(model)
            res = chat(model, prompt, tools=True)
            if kind == "tool":
                ok = expected in res.get("calls", [])
            elif kind == "notool":
                ok = not res.get("calls") and bool(res.get("text"))
            else:
                ok = bool(res.get("text")) and not res.get("calls")
            res.update(kind=kind, prompt=prompt, ok=ok)
            rows.append(res)
            print(f"  [{'OK' if ok else '--'}] {prompt[:40]:40} {res.get('wall',0):5.1f}s tps={res.get('tps',0):5.1f} calls={res.get('calls')} :: {res.get('text','')[:110]!r}"
                  + (f" ERR {res['error']}" if 'error' in res else ""), flush=True)
        _, used = gpu()
        try:
            ps = [m for m in http("/api/ps")["models"] if m["name"] == model]
            vram = ps[0]["size_vram"] // 2**20 if ps else 0
            size = ps[0]["size"] // 2**20 if ps else 0
        except Exception:  # noqa
            vram = size = 0
        http("/api/generate", {"model": model, "keep_alive": 0})  # unload
        time.sleep(3)
        results[model] = {"rows": rows, "vram_mb": vram, "size_mb": size}
        tool_ok = sum(r["ok"] for r in rows if r["kind"] != "chat")
        warm_tps = [r["tps"] for r in rows[1:] if r.get("tps")]
        print(f"  => score {tool_ok}/6, avg tps {sum(warm_tps)/max(1,len(warm_tps)):.1f}, on GPU {vram}/{size} MB", flush=True)

    json.dump(results, open(__file__.replace("bench.py", "bench-results.json"), "w", encoding="utf-8"), ensure_ascii=False, indent=1)


if __name__ == "__main__":
    main()
