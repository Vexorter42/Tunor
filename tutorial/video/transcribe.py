#!/usr/bin/env python3
"""Turns a recording into words with the moment each was said. Used by `build.py fit --asr`.

    python transcribe.py MODEL_DIR recording.wav words.json

Needs the `vosk` package and a Russian model (vosk-model-small-ru-0.22 is enough). It works
offline: the recording does not leave the machine. Run it with the Python that has vosk, with -I
if the model folder came from the internet.
"""
import json
import subprocess
import sys
import wave


def transcribe(model_dir, audio, rate=16000):
    from vosk import KaldiRecognizer, Model, SetLogLevel
    SetLogLevel(-1)
    pcm = subprocess.run(["ffmpeg", "-v", "error", "-i", audio, "-vn", "-ac", "1", "-ar", str(rate), "-f", "s16le", "-"],
                         stdout=subprocess.PIPE, check=True).stdout
    rec = KaldiRecognizer(Model(model_dir), rate)
    rec.SetWords(True)
    words = []
    step = 8000                                         # a quarter of a second of 16-bit samples
    for i in range(0, len(pcm), step):
        if rec.AcceptWaveform(pcm[i:i + step]):
            words += json.loads(rec.Result()).get("result", [])
    words += json.loads(rec.FinalResult()).get("result", [])
    return [{"w": w["word"], "s": round(w["start"], 2), "e": round(w["end"], 2), "c": round(w.get("conf", 1.0), 2)} for w in words]


if __name__ == "__main__":
    model, audio, out = sys.argv[1:4]
    result = transcribe(model, audio)
    with open(out, "w", encoding="utf-8") as f:
        json.dump(result, f, ensure_ascii=False)
    print(f"{len(result)} слов -> {out}")
