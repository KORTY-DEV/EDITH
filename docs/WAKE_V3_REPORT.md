# WAKE V3 OFFLINE REPORT

Generated from exact Python RuntimeWindows and both ONNX stages; no raw audio copied.

Stage1 model: `.\wake-training\custom-wakeword-trainer\edith-stage1.onnx`
Verifier model: `.\wake-training\custom-wakeword-trainer\edith-verifier.onnx`
Corpus: 80 positives, 450 negatives, 30 generated silence cases.

## Joint threshold grid

| stage1 | verifier | real POS | real NEG | silence |
|---:|---:|---:|---:|---:|
| 0.30 | 0.50 | 80/80 | 3/450 | 3/30 |
| 0.30 | 0.60 | 80/80 | 3/450 | 3/30 |
| 0.30 | 0.70 | 80/80 | 3/450 | 3/30 |
| 0.30 | 0.75 | 80/80 | 3/450 | 3/30 |
| 0.30 | 0.80 | 80/80 | 3/450 | 2/30 |
| 0.30 | 0.90 | 80/80 | 3/450 | 2/30 |
| 0.40 | 0.50 | 80/80 | 3/450 | 3/30 |
| 0.40 | 0.60 | 80/80 | 3/450 | 3/30 |
| 0.40 | 0.70 | 80/80 | 3/450 | 3/30 |
| 0.40 | 0.75 | 80/80 | 3/450 | 3/30 |
| 0.40 | 0.80 | 80/80 | 3/450 | 2/30 |
| 0.40 | 0.90 | 80/80 | 3/450 | 2/30 |
| 0.50 | 0.50 | 80/80 | 3/450 | 3/30 |
| 0.50 | 0.60 | 80/80 | 3/450 | 3/30 |
| 0.50 | 0.70 | 80/80 | 3/450 | 3/30 |
| 0.50 | 0.75 | 80/80 | 3/450 | 3/30 |
| 0.50 | 0.80 | 80/80 | 3/450 | 2/30 |
| 0.50 | 0.90 | 80/80 | 3/450 | 2/30 |
| 0.60 | 0.50 | 80/80 | 3/450 | 3/30 |
| 0.60 | 0.60 | 80/80 | 3/450 | 3/30 |
| 0.60 | 0.70 | 80/80 | 3/450 | 3/30 |
| 0.60 | 0.75 | 80/80 | 3/450 | 3/30 |
| 0.60 | 0.80 | 80/80 | 3/450 | 2/30 |
| 0.60 | 0.90 | 80/80 | 3/450 | 2/30 |
| 0.70 | 0.50 | 80/80 | 3/450 | 3/30 |
| 0.70 | 0.60 | 80/80 | 3/450 | 3/30 |
| 0.70 | 0.70 | 80/80 | 3/450 | 3/30 |
| 0.70 | 0.75 | 80/80 | 3/450 | 3/30 |
| 0.70 | 0.80 | 80/80 | 3/450 | 2/30 |
| 0.70 | 0.90 | 80/80 | 3/450 | 2/30 |

## Selected policy (Stage1 0.70, verifier 0.75)

Real positives: 80/80
Real negatives: 3/450
Synthetic silence: 3/30

| negative category | Stage1 candidates | verifier accepts | final triggers |
|---|---:|---:|---:|
| edithhard | 9/90 | 3/90 | 2/90 |
| keyboard | 6/60 | 0/60 | 0/60 |
| media | 4/90 | 0/90 | 0/90 |
| music | 9/60 | 0/60 | 0/60 |
| normalspeech | 9/90 | 0/90 | 0/90 |
| room | 2/60 | 1/60 | 1/60 |

## Comparison with existing engines

| engine | real-positive set | real-negative triggers | silence | status |
|---|---:|---:|---:|---|
| Vosk | not re-run in this offline acoustic report | text engine; not comparable to acoustic score | not applicable | retained fallback |
| Hybrid Fusion V2 | 50/50 | 3/450 at 0.70/2 | not measured | current production fallback |
| old custom | 80/80 in the last scan | 8/450 at 0.70/2 | not accepted | candidate generator only |
| Wake V3 Stage 1 | 80/80 | 8/450 at 0.70/2 | 17/30 | FAILED standalone acceptance |
| Wake V3 Stage 1 + verifier | 80/80 | 3/450 at 0.70/2 | 3/30 | FAILED required gate |

The verifier removes five of the eight Stage-1 real-negative clips. The remaining
false triggers are two `edithhard` clips and one `room` clip; they remain blockers.
The 17/30 Stage-1 silence triggers are reduced to 3/30 after verification, but
three is not zero and must not be hidden by a VAD or config threshold.

## Silence investigation and Python/C# parity

The first divergent layer was the exported `melspectrogram.onnx` when Python
passed multiple 1760-sample windows as one batch. Its dynamic batch dimension is
not numerically batch-safe: later rows differed by as much as 63.58 from the
same windows run one at a time. C# already invokes one window per 80 ms chunk.
`RuntimeWindows` now uses the same sequential mel cadence; no model, threshold,
corpus, or production configuration was changed.

The debug-only `WakeParitySnapshot`/`WakeCheck --parity-out` path dumps raw PCM
window, mel output, transformed mel history, embedding history, classifier
features, and score. On the fixed all-zero 2 s fixture (25 chunks), the maximum
Python/C# deltas were: mel output 1.70e-6, mel history 2.38e-7, embedding output
1.83e-6, features 1.84e-6, and Stage-1 score 8.94e-8. The parity regression
`scripts/test_runtime_parity_v3.py` also passes the zero fixture.

## Resource and acceptance decision

The post-parity joint run remains below the required gate:

* real-positive recall: 80/80 (100%);
* real-negative false triggers: 3/450 (must be ≤1);
* synthetic silence false triggers: 3/30 (must be 0/30).

Production remains on the user-selected existing engine. `config/wake.json` was
preserved, and `custom-v3` models were not installed under the production model
directory. No live run is authorized while this offline gate is failed.

## Python / C# Stage-1 parity sample after fix

| input | Python max | C# max | absolute delta |
|---|---:|---:|---:|
| pos_001.wav | 0.999999 | 1.000000 | 1.3113e-06 |
| pos_002.wav | 0.999999 | 1.000000 | 1.19209e-06 |
| pos_003.wav | 1.000000 | 1.000000 | 2.98023e-07 |
| pos_004.wav | 1.000000 | 1.000000 | 3.57628e-07 |
| neg_edithhard_0001.wav | 0.002469 | 0.002500 | 3.07583e-05 |
| neg_edithhard_0002.wav | 0.001553 | 0.001600 | 4.71798e-05 |
| neg_edithhard_0003.wav | 0.000627 | 0.000600 | 2.72196e-05 |
| silence0.wav | 0.012963 | 0.013000 | 3.70629e-05 |
| silence1.wav | 0.302047 | 0.302000 | 4.72963e-05 |
| silence2.wav | 0.999996 | 1.000000 | 3.51667e-06 |

The displayed C# values are rounded to four decimals by WakeCheck; the full
tensor fixture comparison above is the accepted parity evidence.

## Verification status

Release project tests that ran independently: Core 104/104, Speech 23/23, Audio
174/174, App 4/4. A full solution build/test was blocked only because the already
running `Edith.App.exe` (PID 1184) held `src/Edith.App/bin/Release/.../Edith.Audio.dll`;
no process was terminated. The V3 runtime build itself completed successfully.

## Wake V3 continuation (2026-09-20)

This section supersedes the pre-retraining failure verdict above. Production
configuration and installed models were not changed. Stage1 and the existing
20k synthetic-positive corpus were reused unchanged.

### File-level split and verifier training

`data/diagnostics/wake-v3-frozen-split.json` is the explicit file-level manifest:
360 negative WAVs are verifier-train only, 45 are calibration-only, and 45 are
final held-out negatives. The 40 legacy train-side positives used for tiny
verifier fine-tuning are excluded from the held-out positive result; the
remaining 40 positives are untouched by verifier training. Historical verifier
provenance was not recorded, so the 80/450 corpus result is labelled historical
and is not treated as an independent estimate.

The selected model is the unchanged tiny verifier architecture, initialized from
the existing verifier and fine-tuned for 3000 CPU/GPU steps (learning rate
2e-5). Stage1 stayed frozen; mining used only the 360 train-side negatives,
with generated noise/silence negatives. No new recordings or positive corpus
were created.

### Threshold selection and first frozen result

The old verifier threshold-only sweep on the full historical corpus could not
pass the gate: the best point in the bounded grid was 0.9995 with 77/80
positives, 1/450 negatives, and 0/30 silence. On the calibration split, the
selected verifier threshold 0.94 retained 10/10 validation positives, 0/45
calibration negatives, and 0/30 generated silence.

The first frozen held-out run of `edith-verifier-finetuned6.onnx`, with the
production Stage1 threshold fixed at 0.70, is:

| split | accepted / total |
|---|---:|
| untouched positives | 40/40 |
| calibration negatives | 0/45 |
| final held-out negatives | 0/45 |
| generated silence | 0/30 |
| historical full corpus (non-independent) | 80/80, 0/450, 0/30 |

The three historical real false triggers were `neg_edithhard_0027.wav`
(Stage1 0.9999925, verifier 0.9999977), `neg_edithhard_0028.wav`
(0.9999683, 0.9999121), and `neg_room_0042.wav`
(0.9999967, 0.9922999). The three historical silence triggers were
`silence_05` (RMS 0.0100033, verifier 0.8707688), `silence_28` (RMS
0.0030057, verifier 0.9998869), and `silence_29` (RMS 0.0100115, verifier
0.9997635). These are audible low-level noise fixtures rather than near-zero
digital silence; no energy guard was added because positive RMS overlaps this
range.

### Fresh sanity, parity, and cost

The required one-shot fresh-negative sanity used 200 generated two-second clips
(exactly 5000 runtime windows, seed 20260930). It produced 2/200 clip-level
triggers, both in the higher-noise levels; this is recorded as a robustness
warning and was not used to tune the frozen result.

Python/C# verifier parity on 10 positives, 10 negatives, and 10 silence cases
passed: Stage1 maximum absolute delta 2.5979214e-6 and verifier maximum delta
3.2892822e-6 (both below 1e-5). A representative WakeCheck run reported
25 chunks and 1.830 ms average chunk time with verifier enabled; both ONNX
files are 857,039 bytes. The release WakeCheck build succeeded and the focused
Audio suite is 174/174.

### Current decision

The explicit frozen offline acceptance gate passes (40/40 untouched positives,
0/45 calibration negatives, 0/45 final negatives, 0/30 silence; historical
full scan also 80/80, 0/450, 0/30). Live/production rollout remains paused in
this task because the fresh generated-noise sanity is not zero; no production
threshold or configuration was changed.

