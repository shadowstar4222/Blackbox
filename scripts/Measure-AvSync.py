"""Measure the smoke test's central flash against its 1 kHz pulse, without playback."""
import argparse
import json
import subprocess
from pathlib import Path

import numpy as np

parser = argparse.ArgumentParser()
parser.add_argument("directory", type=Path)
parser.add_argument("ffmpeg", type=Path)
parser.add_argument("--summary", action="store_true")
parser.add_argument("--max-drift-ms", type=float)
args = parser.parse_args()
probe = args.ffmpeg.with_name("ffprobe.exe")
results = []
for path in sorted(args.directory.glob("*.mkv")):
    if path.name.endswith(".active.mkv"):
        continue
    metadata = json.loads(subprocess.check_output([
        str(probe), "-v", "error", "-show_streams", "-show_format", "-of", "json", str(path)
    ]))
    times = json.loads(subprocess.check_output([
        str(probe), "-v", "error", "-select_streams", "v:0", "-show_frames",
        "-show_entries", "frame=best_effort_timestamp_time", "-of", "json", str(path)
    ]))["frames"]
    pixels = np.frombuffer(subprocess.check_output([
        str(args.ffmpeg), "-v", "error", "-i", str(path), "-an", "-vf",
        "crop=80:80:360:185,scale=1:1", "-pix_fmt", "gray", "-fps_mode", "passthrough",
        "-f", "rawvideo", "pipe:1"
    ]), dtype=np.uint8)
    pcm = np.frombuffer(subprocess.check_output([
        str(args.ffmpeg), "-v", "error", "-i", str(path), "-vn", "-af", "bandpass=f=1000:w=150",
        "-ar", "48000", "-ac", "1", "-f", "f32le", "pipe:1"
    ]), dtype="<f4")
    count = len(pcm) // 480
    power = np.sqrt(np.mean(pcm[:count * 480].reshape(-1, 480) ** 2, axis=1))
    video_on = pixels > 160
    audio_on = power > .015
    video_edges = [float(times[index]["best_effort_timestamp_time"])
                   for index in range(1, min(len(times), len(pixels))) if video_on[index] and not video_on[index - 1]]
    audio_start = float(next(stream for stream in metadata["streams"] if stream["codec_type"] == "audio").get("start_time", 0))
    audio_edges = [audio_start + index / 100 for index in range(1, count) if audio_on[index] and not audio_on[index - 1]]
    pairs = []
    for video in video_edges:
        if audio_edges:
            audio = min(audio_edges, key=lambda value: abs(value - video))
            if abs(audio - video) < .9:
                pairs.append({"video": video, "audio": audio, "audio_minus_video_ms": round((audio - video) * 1000, 2)})
    results.append({"file": path.name, "duration": metadata["format"]["duration"],
                    "pairs": pairs, "video_flashes": len(video_edges), "audio_pulses": len(audio_edges),
                    "median_offset_ms": float(np.median([pair["audio_minus_video_ms"] for pair in pairs])) if pairs else None})
offsets = [pair["audio_minus_video_ms"] for result in results for pair in result["pairs"]]
drift = float(np.median(offsets[-10:]) - np.median(offsets[:10])) if len(offsets) >= 20 else None
report = {"segments": len(results), "matched_pulses": len(offsets), "drift_ms": drift,
          "segment_median_offsets_ms": [result["median_offset_ms"] for result in results]}
if not args.summary:
    report["details"] = results
print(json.dumps(report, indent=2))
if args.max_drift_ms is not None and (drift is None or abs(drift) > args.max_drift_ms):
    raise SystemExit("Audio/video drift check failed")
