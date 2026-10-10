"""Отчёт по пробуждениям «Эдит» за период: сколько раз EDITH проснулась, сколько раз после этого
прозвучала команда (значит, это были вы), сколько раз тишина (вероятно, ложное срабатывание)
и что именно отклонила проверка GigaAM.

    python scripts/wake_report.py            # последние 24 часа
    python scripts/wake_report.py 72         # последние 72 часа
"""

from __future__ import annotations

import json
import sqlite3
import sys
from collections import Counter
from datetime import datetime, timedelta, timezone

DB = r"C:\EDITH\data\journal\edith-journal.db"
COMMAND_WINDOW_S = 15


def parse(ts: str) -> datetime:
    return datetime.fromisoformat(ts)


def main() -> int:
    hours = float(sys.argv[1]) if len(sys.argv) > 1 else 24
    since = (datetime.now(timezone.utc) - timedelta(hours=hours)).isoformat()

    con = sqlite3.connect(DB)
    rows = con.execute(
        "SELECT ts, kind, summary, data FROM events "
        "WHERE ts >= ? AND kind IN ('voice.wake','voice.wake-rejected','voice.command','voice.transcript','app.started') "
        "ORDER BY id",
        (since,),
    ).fetchall()

    accepted, rejected, commands = [], [], []
    for ts, kind, summary, data in rows:
        t = parse(ts)
        info = json.loads(data or "{}")
        if kind == "voice.wake":
            accepted.append((t, summary, info.get("source", "?")))
        elif kind == "voice.wake-rejected":
            rejected.append((t, summary))
        elif kind == "voice.command":
            commands.append(t)

    used, silent = [], []
    for t, heard, source in accepted:
        if any(timedelta(0) <= c - t <= timedelta(seconds=COMMAND_WINDOW_S) for c in commands):
            used.append((t, heard, source))
        else:
            silent.append((t, heard, source))

    span_h = max(hours, 1e-9)
    print(f"Период: последние {hours:g} ч, перезапусков EDITH: {sum(1 for r in rows if r[1] == 'app.started')}")
    print(f"Пробуждений принято: {len(accepted)}  (с командой: {len(used)}, без команды: {len(silent)})")
    print(f"Отклонено проверкой GigaAM: {len(rejected)}")
    print(f"Подозрительных (приняты, но тишина) в сутки: {len(silent) * 24 / span_h:.1f}  (цель: не больше 1)")

    if silent:
        print("\nПринятые без команды (возможные ложные):")
        for t, heard, source in silent[-15:]:
            print(f"  {t.astimezone():%d.%m %H:%M:%S}  [{source}]  услышала: «{heard}»")

    if rejected:
        words = Counter(h for _, h in rejected if h and h != "(тишина)")
        quiet = sum(1 for _, h in rejected if h == "(тишина)")
        print(f"\nОтклонённые: тишина {quiet}, слова {sum(words.values())}. Чаще всего слышала:")
        for word, count in words.most_common(10):
            print(f"  {count:3d} × «{word}»")

    per_hour = Counter(t.astimezone().hour for t, _, _ in accepted)
    if per_hour:
        print("\nПробуждения по часам: " + ", ".join(f"{h:02d}ч:{n}" for h, n in sorted(per_hour.items())))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
