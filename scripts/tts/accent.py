"""Automatic Russian stress for EDITH's Silero voice (RUAccent, runs offline on CPU).

The text arrives from C# already prepared: numbers spelled out, and words from
config/stress.json carry their own "+" mark. Those hand-set marks always win; RUAccent
fills in every other word (and resolves homographs by context: "зАмок"/"замОк").
SSML tags (<speak>, <prosody>) are passed through untouched.
"""

from __future__ import annotations

import os
import re

# The models live on disk (C:\EDITH\models\ruaccent); never reach for the network.
os.environ.setdefault("HF_HUB_OFFLINE", "1")

_VOWELS = re.compile(r"[аеёиоуыэюя]")
_TAG = re.compile(r"(<[^>]+>)")
_WORD = re.compile(r"[A-Za-zА-Яа-яЁё+]+(?:-[A-Za-zА-Яа-яЁё+]+)*")


class Accentizer:
    def __init__(self, workdir: str, model: str = "tiny2.1") -> None:
        from ruaccent import RUAccent

        self._engine = RUAccent()
        self._engine.load(omograph_model_size=model, use_dictionary=True, workdir=workdir)

    def apply(self, text: str) -> str:
        return "".join(
            part if _TAG.fullmatch(part) else self._plain(part)
            for part in _TAG.split(text)
        )

    def _plain(self, text: str) -> str:
        if not re.search(r"[А-Яа-яЁё]", text):
            return text

        # Words that already carry a hand-set "+": remember them, let RUAccent see them plain.
        manual: dict[str, str] = {}
        for word in _WORD.findall(text):
            if "+" in word:
                manual[word.replace("+", "").lower()] = word
        plain = text.replace("+", "") if manual else text

        try:
            stressed = self._engine.process_all(plain)
        except Exception:
            return text

        original_words = _WORD.findall(plain)
        stressed_words = _WORD.findall(stressed)
        if len(original_words) != len(stressed_words):
            return text  # alignment lost: keep the hand-marked text as it is

        fixed = []
        for word in stressed_words:
            key = word.replace("+", "").lower()
            if key in manual:
                fixed.append(_with_case(manual[key], word))
            elif len(_VOWELS.findall(key)) == 1:
                # One syllable is always stressed; a mark only makes "на", "в", "как" sound shouted.
                fixed.append(word.replace("+", ""))
            else:
                fixed.append(word)
        stressed_words = fixed

        it = iter(stressed_words)
        return _WORD.sub(lambda _: next(it), stressed)


def _with_case(manual_word: str, like: str) -> str:
    plain = like.replace("+", "")
    if plain[:1].isupper() and manual_word[:1].islower():
        return manual_word[:1].upper() + manual_word[1:]
    return manual_word
