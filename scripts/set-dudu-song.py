#!/usr/bin/env python3
"""Replace the song Dudu sings on "sing for me" with an audio file you supply.

Converts the file with ffmpeg to the pack format (mono 48 kHz 16-bit PCM WAV,
peak-normalized to -1.5 dBFS, 10 ms fades), writes it to the private audio
pack, and updates the pack manifest and the provenance record.

    python3 scripts/set-dudu-song.py <audio-file> --source "<where it came from>"

Only use audio you have the rights to use; the pack stays private-use only.
"""
import argparse
import hashlib
import json
import pathlib
import subprocess
import sys
import tempfile
import wave
from datetime import datetime, timezone

REPO = pathlib.Path(__file__).resolve().parent.parent
PACK_ROOT = REPO / "src/Dudu.App/Assets/Audio/private-dudu"
MANIFEST = PACK_ROOT / "manifest.json"
PROVENANCE = REPO / "assets/sources/private-dudu-audio.json"
SONG_PACK = "dudu-song"
SONG_FILE = f"{SONG_PACK}/song-01.wav"
MAX_SONG_MS = 60_000  # AudioManifestContract.MaxSongDurationMs
MAX_SONG_BYTES = 6_291_456  # AudioManifestContract.MaxSongFileBytes


def run(*args: str) -> str:
    return subprocess.run(args, check=True, capture_output=True, text=True).stderr


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("audio", type=pathlib.Path)
    parser.add_argument("--source", required=True, help="where the audio came from, for the provenance record")
    parser.add_argument("--start", type=float, default=0.0, help="start offset in seconds")
    parser.add_argument("--duration", type=float, help="seconds to keep (max 60)")
    args = parser.parse_args()

    keep = min(args.duration or MAX_SONG_MS / 1000, MAX_SONG_MS / 1000)
    with tempfile.TemporaryDirectory() as tmp:
        raw = pathlib.Path(tmp) / "raw.wav"
        run("ffmpeg", "-y", "-v", "error", "-ss", str(args.start), "-t", str(keep), "-i", str(args.audio),
            "-ac", "1", "-ar", "48000", "-c:a", "pcm_s16le", str(raw))
        peak_db = float(next(
            line.split("max_volume:")[1].split("dB")[0]
            for line in run("ffmpeg", "-v", "info", "-i", str(raw), "-af", "volumedetect", "-f", "null", "-").splitlines()
            if "max_volume:" in line))
        with wave.open(str(raw)) as w:
            seconds = w.getnframes() / w.getframerate()
        out = PACK_ROOT / SONG_FILE
        run("ffmpeg", "-y", "-v", "error", "-i", str(raw), "-af",
            f"volume={-1.5 - peak_db}dB,afade=t=in:d=0.01,afade=t=out:st={max(0.0, seconds - 0.01)}:d=0.01",
            "-ac", "1", "-ar", "48000", "-c:a", "pcm_s16le", "-map_metadata", "-1", "-fflags", "+bitexact",
            "-flags:a", "+bitexact", str(out))

    data = out.read_bytes()
    if len(data) > MAX_SONG_BYTES:
        sys.exit(f"song is {len(data)} bytes; the pack allows {MAX_SONG_BYTES}")
    with wave.open(str(out)) as w:
        duration_ms = round(w.getnframes() * 1000 / w.getframerate())
    sha = hashlib.sha256(data).hexdigest()

    manifest = json.loads(MANIFEST.read_text())
    song = next(pack for pack in manifest["Packs"] if pack["PackId"] == SONG_PACK)
    song["Cues"] = [{"CueId": "song-01", "FilePath": SONG_FILE, "DurationMs": duration_ms, "Sha256": sha}]
    MANIFEST.write_text(json.dumps(manifest, indent=2, ensure_ascii=False) + "\n")

    provenance = json.loads(PROVENANCE.read_text())
    provenance["sources"] = [s for s in provenance["sources"] if not s["id"].startswith("dudu-song")]
    provenance["sources"].append({
        "id": "dudu-song",
        "pageUrl": args.source,
        "accessedUtc": datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ"),
        "originalFile": args.audio.name,
        "originalSha256": hashlib.sha256(args.audio.read_bytes()).hexdigest(),
        "derivedCues": [{
            "cueId": f"{SONG_PACK}/song-01",
            "sourceRange": {"startMs": round(args.start * 1000), "endMs": round(args.start * 1000) + duration_ms, "label": "song"},
            "derivedFile": SONG_FILE,
            "derivedDurationMs": duration_ms,
            "derivedSha256": sha,
            "transform": "Decoded source; converted to mono 48 kHz, 16-bit PCM WAV; peak-normalized to -1.5 dBFS; applied 10 ms fade-in and fade-out.",
        }],
    })
    PROVENANCE.write_text(json.dumps(provenance, indent=2, ensure_ascii=False) + "\n")
    print(f"dudu-song: {duration_ms} ms, {len(data)} bytes, sha256 {sha}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
