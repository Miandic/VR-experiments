"""Exercise an installed Development APK over ADB and save local-only diagnostics."""
import json
import os
from pathlib import Path
import statistics
import subprocess
import time

ROOT = Path(__file__).resolve().parents[1]
OUTPUT = ROOT / ".device-check"
ADB = r"C:\Program Files\Meta VR Developer Hub\resources\bin\adb.exe"
FILES = "/sdcard/Android/data/com.miandic.questdesktop/files/"


def adb(*args):
    serial = os.environ.get("QUEST_DESKTOP_SERIAL", "")
    target = ["-s", serial] if serial else []
    return subprocess.check_output([ADB, *target, *args], stderr=subprocess.STDOUT, text=True)


def state():
    return json.loads(adb("shell", "cat", FILES + "device-state.json"))


def action(name):
    command = OUTPUT / "command.json"
    command.write_text(json.dumps({"action": name}), encoding="utf-8")
    adb("push", str(command), FILES + "device-command.json")
    deadline = time.monotonic() + 8
    while time.monotonic() < deadline:
        time.sleep(0.3)
        result = state()
        if result["lastAction"] == name:
            return result
    raise TimeoutError("Device action did not complete: " + name)


def screenshot(name):
    adb("shell", "screencap", "-p", "/sdcard/questdesktop-check.png")
    adb("pull", "/sdcard/questdesktop-check.png", str(OUTPUT / (name + ".png")))


def main():
    OUTPUT.mkdir(exist_ok=True)
    deadline = time.monotonic() + 35
    current = {}
    while time.monotonic() < deadline:
        try:
            current = state()
            if current["status"] == "Connected" and current["width"] > 2:
                break
        except (subprocess.CalledProcessError, json.JSONDecodeError):
            pass
        time.sleep(1)
    else:
        raise RuntimeError("No stream on Quest: " + str(current))
    if current["hud"]:
        current = action("World / HUD")
    assert not current["screenFollowsHead"]
    near = action("Bring near")
    assert near["screenWidth"] <= 0.81
    assert action("World / HUD")["screenFollowsHead"]
    screenshot("hud-stream")
    action("Hide menu")
    assert not state()["menuVisible"]
    action("Show menu")
    assert state()["menuVisible"]
    assert not action("World / HUD")["screenFollowsHead"]
    action("Recenter")
    screenshot("world-stream")
    samples = []
    for _ in range(20):
        time.sleep(1)
        item = state()
        assert item["status"] == "Connected", item
        samples.append(item)
    summary = {
        "resolution": [samples[-1]["width"], samples[-1]["height"]],
        "samples": len(samples),
        "stream_fps_median": statistics.median(s["streamFps"] for s in samples),
        "stream_fps_min": min(s["streamFps"] for s in samples),
        "mbps_median": statistics.median(s["mbps"] for s in samples),
        "decode_ms_median": statistics.median(s["decodeMs"] for s in samples),
        "frame_time_ms_median": statistics.median(s["frameTimeMs"] for s in samples),
        "checks": "world/HUD binding, bring near, menu visibility, sustained decode; hand gestures require physical input",
        "hand_actions": samples[-1].get("handActions", 0),
        "last_hand_action": samples[-1].get("lastHandAction", ""),
    }
    (OUTPUT / "samples.json").write_text(json.dumps(samples, indent=2), encoding="utf-8")
    (OUTPUT / "summary.json").write_text(json.dumps(summary, indent=2), encoding="utf-8")
    print(json.dumps(summary, indent=2))


if __name__ == "__main__":
    main()
