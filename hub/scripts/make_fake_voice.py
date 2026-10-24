"""Regenerate agent/assets/fake_voice_24k.pcm: ~1.8 s of chirpy "gator babble" (PCM16 LE mono, 24 kHz).

VOICE_PROVIDER=fake streams this as the pet's reply so Unity playback and the talk animation can be
tested without an xAI key. Deterministic, stdlib only:  python scripts/make_fake_voice.py
"""

from __future__ import annotations

import math
import random
import sys
from array import array
from pathlib import Path

RATE = 24000
OUT = Path(__file__).resolve().parents[1] / "agent" / "assets" / "fake_voice_24k.pcm"


def syllable(f0: float, f1: float, dur: float, amp: float) -> list[float]:
    n = int(dur * RATE)
    out, phase = [], 0.0
    for i in range(n):
        t = i / n
        f = f0 + (f1 - f0) * t + 12.0 * math.sin(2 * math.pi * 7.0 * i / RATE)  # glide + vibrato
        phase += 2 * math.pi * f / RATE
        # a few harmonics, brighter in the middle of the syllable (vowel-ish)
        s = math.sin(phase) + 0.45 * math.sin(2 * phase) + 0.25 * (0.5 + t) * math.sin(3 * phase) \
            + 0.1 * math.sin(5 * phase)
        env = min(1.0, t / 0.12) * min(1.0, (1.0 - t) / 0.3)
        out.append(amp * env * s / 1.8)
    return out


def main() -> None:
    rng = random.Random(7)
    samples: list[float] = [0.0] * int(0.05 * RATE)
    for k in range(10):
        base = rng.uniform(360, 470)
        rise = rng.uniform(-90, 140) if k < 9 else -160  # last syllable falls, like the end of a sentence
        samples += syllable(base, base + rise, rng.uniform(0.10, 0.19), rng.uniform(0.45, 0.8))
        samples += [0.0] * int(rng.uniform(0.02, 0.07) * RATE)
    samples += [0.0] * int(0.08 * RATE)
    pcm = array("h", (max(-32768, min(32767, int(x * 0.6 * 32767))) for x in samples))
    if sys.byteorder == "big":
        pcm.byteswap()  # the wire format is little-endian
    OUT.parent.mkdir(parents=True, exist_ok=True)
    OUT.write_bytes(pcm.tobytes())
    print(f"wrote {OUT} ({len(pcm) / RATE:.2f} s, {OUT.stat().st_size} bytes)")


if __name__ == "__main__":
    main()
