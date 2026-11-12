"""Profile the existing host capture/encode path without saving desktop images."""
import argparse
import json
from pathlib import Path
import statistics
import sys
import time

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "pc-host"))
from host import encode_frame
import mss


def percentile(values, fraction):
    return sorted(values)[round((len(values) - 1) * fraction)]


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--frames", type=int, default=30)
    parser.add_argument("--monitor", type=int, default=1)
    parser.add_argument("--quality", type=int, default=97)
    args = parser.parse_args()
    if args.frames < 5:
        parser.error("at least 5 frames required")
    elapsed, grabs, sizes = [], [], []
    with mss.MSS() as capture:
        class TimedCapture:
            def grab(self, monitor):
                started = time.perf_counter()
                shot = capture.grab(monitor)
                self.elapsed = (time.perf_counter() - started) * 1000
                return shot
        timed = TimedCapture()
        monitor = capture.monitors[args.monitor]
        for index in range(args.frames + 3):
            started = time.perf_counter()
            width, height, jpeg = encode_frame(timed, monitor, args.quality, 0)
            total = (time.perf_counter() - started) * 1000
            if index >= 3:
                elapsed.append(total); grabs.append(timed.elapsed); sizes.append(len(jpeg))
    print(json.dumps({
        "resolution": [width, height], "frames": args.frames, "quality": args.quality,
        "capture_ms_p50": statistics.median(grabs),
        "capture_and_jpeg_ms_p50": statistics.median(elapsed),
        "capture_and_jpeg_ms_p95": percentile(elapsed, 0.95),
        "jpeg_bytes_p50": statistics.median(sizes),
        "estimated_jpeg_mbps_at_60_same_content": statistics.median(sizes) * 8 * 60 / 1e6,
        "note": "Current desktop content only; excludes network, Quest decode and GPU upload. No images saved."
    }, indent=2))


if __name__ == "__main__":
    main()
