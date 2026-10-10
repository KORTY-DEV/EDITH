# EDITH custom wake word runtime

`config\wake.json` выбирает движок wake-word. Код: `src\Edith.Audio\Wake`.

## Как это устроено

```
MicrophoneMonitor (MCHOSE S9 PRO, mono 16 kHz float)      <- единственный capture
   |
   v
App.OnSamplesReady --WaitingWake--> WakeRuntime.AcceptWakeAudio(samples, n, vad.IsSpeech)
                   --WakeActive---> WakeRuntime.AcceptActiveAudio   (транскрипция фразы)
                                        |
                  +---------------------+----------------------+
                  |                                            |
          OnnxWakeDetector (custom)                   VoskWakeDetector
   mel.onnx -> embedding.onnx -> 16x96 window            WakeWordDetector + WakePhraseMatcher
   -> edith.onnx -> WakeDetectionPolicy                  (прежняя логика, без изменений)
```

* `engine: auto` — custom, если `edith.onnx`, `melspectrogram.onnx`, `embedding_model.onnx`
  существуют, проходят проверку контракта и реальный прогон pipeline; иначе Vosk (`vosk-fallback`).
* `engine: custom` — то же, но при ошибке fallback только если `fallbackToVosk = true`.
* `engine: custom-v3` (also accepted as `wake-v3`) — production two-stage KWS: a new
  openWakeWord-style Stage 1 in `customV3Model` emits an acoustic candidate; the local ONNX
  `verifierModel` receives that exact live 16×96 embedding window and must exceed
  `verifierThreshold`. Vosk neither opens nor confirms this path. The two model files live
  under `models\\wake\\custom-v3` and are separate from the existing custom/Hybrid fallback.
* `engine: vosk` — принудительно старый wake (`forced-vosk`), ONNX не загружается.
* `engine: hybrid` — один и тот же MCHOSE/VAD audio chunk одновременно получает Vosk и
  custom ONNX. Strong Vosk (`эдит` / `эди` и ASR-нормализованные exact aliases) открывает
  session сразу. Custom **никогда** не открывает session сам: он подтверждает только одиночный
  weak token `едет` в том же VAD speech segment и в окне 1 s. Multi-word phrases вроде
  `едет дождь` не являются wake.
* Custom-режим: пока ждём wake, Vosk **не получает аудио** (экономия CPU). При срабатывании
  runtime отдаёт Vosk pre-roll (начало произнесения из кольцевого буфера в памяти), и
  «Эдит открой блокнот» одной фразой работает как раньше.
* Custom wake принимается, только если Silero VAD видел речь за последние 3 s
  (защита от ложных срабатываний модели на шумовом поле микрофона).
* Установка модели подхватывается запущенным EDITH сама (опрос файлов раз в 3 s,
  нужны две одинаковые проверки подряд), без перезапуска. Сбой custom в работе = откат на Vosk.

## Контракт модели (взят из `train.py` и `openwakeword/utils.py`, сверен с metadata)

| модель | вход | выход |
|---|---|---|
| `melspectrogram.onnx` | `input` `[batch, samples]` float32, шкала int16 | `[time, 1, frames, 32]` |
| `embedding_model.onnx` | `input_1` `[n, 76, 32, 1]` | `[n, 1, 1, 96]` |
| `edith.onnx` | `[1, 16, 96]` float32 | `probability` `[1, 1]`, sigmoid внутри модели |

Streaming, как в openWakeWord: шаг 80 ms (1280 samples), mel-окно 1280+480 samples -> 8 кадров,
`x / 10 + 2`, embedding по последним 76 mel-кадрам, окно из 16 embedding-кадров (oldest -> newest).
Сверено с Python-референсом: расхождение score 0.0000 после прогрева.

## Установка модели

```powershell
C:\EDITH\scripts\Install-WakeModel.ps1 -ModelPath C:\EDITH\wake-training\custom-wakeword-trainer\edith.onnx
```

Проверяет файл и контракт (тем же кодом, что runtime), делает backup в `models\wake\custom\backup`,
заменяет `edith.onnx` атомарно, при необходимости ставит generic mel/embedding.
`-WhatIf` — сухой прогон, `-GenericOnly` — только generic модели.

## Diagnostics (`C:\EDITH\data\diagnostics`)

| файл | что внутри |
|---|---|
| `wake-engine.txt` | первая строка: `custom` / `vosk-fallback` / `forced-vosk` / `none`, причина, warnings конфига |
| `wake-model-status.txt` | найденные модели, валидность, input/output metadata, время загрузки, версия ORT |
| `wake-last-score.txt` | последний значимый пик score и threshold (без записи на каждый кадр) |
| `wake-last-detection.txt` | время, движок, score |
| `wake-hybrid.txt` | strong/weak fusion outcome, Vosk token, custom-only rejection, score/hits, VAD (без audio) |
| `wake-v3.txt` | Stage 1 score/candidate count, verifier score, accept/reject reason and verifier latency (без audio) |
| `wake-error.txt` | последняя ошибка (`none`, если ошибок нет) |

## Офлайн-проверка без запуска EDITH

```powershell
$wc = 'C:\EDITH\src\Edith.WakeCheck\bin\Release\net10.0-windows\Edith.WakeCheck.exe'
& $wc validate --model <edith.onnx>            # контракт + прогон pipeline
& $wc score --wav clip.wav --model <edith.onnx>  # score по каждым 80 ms
& $wc policy-report                              # 50 positive / 450 negative, policy 0.55–0.70 × hits 1/2
& $wc live --config C:\temp\hybrid-wake.json --seconds 60 --expected 10 --verbose
```

`hybrid-wake.json` — явный временный config override с `"engine": "hybrid"`; production
`config\wake.json` не меняется. Live summary reports strong Vosk, weak Vosk+custom,
custom-only rejected candidates, misses (against `--expected`) and VAD rejects; it never opens
browser UI or records raw audio.

## Wake V3 acceptance config (does not switch production)

Create a separate file, for example `C:\\temp\\wake-v3.json`; do not replace
`config\\wake.json` until the live protocol is accepted.

```json
{
  "engine": "custom-v3",
  "customV3Model": "C:\\EDITH\\models\\wake\\custom-v3\\edith-stage1.onnx",
  "verifierModel": "C:\\EDITH\\models\\wake\\custom-v3\\edith-verifier.onnx",
  "melModel": "C:\\EDITH\\models\\wake\\custom\\melspectrogram.onnx",
  "embeddingModel": "C:\\EDITH\\models\\wake\\custom\\embedding_model.onnx",
  "threshold": 0.70,
  "verifierThreshold": 0.75,
  "minConsecutiveHits": 2,
  "cooldownMs": 850,
  "fallbackToVosk": true
}
```

```powershell
& $wc live --engine wake-v3 --config C:\temp\wake-v3.json --seconds 90 --expected 30
```

The live command reports Stage 1 candidates and verifier accepts/rejects. `false-wakes` is
intentionally marked for manual review: the tool cannot infer whether a person said the wake
word, so it must not fabricate that measurement.
