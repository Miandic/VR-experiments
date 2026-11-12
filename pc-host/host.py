"""Read-only desktop streaming host. One authenticated viewer, bounded frame flow."""
import argparse
import hmac
import io
import json
import logging
from pathlib import Path
import socket
import ssl
import threading
import time

from protocol import FRAME, MAX_FRAME, read_exact, receive_json, send_json


def encode_frame(capture, monitor, quality, max_width):
    from PIL import Image
    shot = capture.grab(monitor)
    image = Image.frombytes("RGB", shot.size, shot.bgra, "raw", "BGRX")
    if max_width and image.width > max_width:
        image = image.resize((max_width, max(1, round(image.height * max_width / image.width))),
                             Image.Resampling.LANCZOS)
    output = io.BytesIO()
    image.save(output, format="JPEG", quality=quality, subsampling=0, optimize=False)
    return image.width, image.height, output.getvalue()


def serve_viewer(stream, token, args):
    auth = receive_json(stream)
    supplied = auth.get("token")
    if (auth.get("protocol") != "QDS1" or not isinstance(supplied, str)
            or not hmac.compare_digest(supplied.encode(), token.encode())):
        send_json(stream, {"ok": False, "error": "Authentication failed"})
        return
    import mss
    with mss.mss() as capture:
        if not 1 <= args.monitor < len(capture.monitors):
            send_json(stream, {"ok": False, "error": "Monitor unavailable"})
            return
        monitor = capture.monitors[args.monitor]
        if monitor["width"] > 8192 or monitor["height"] > 8192:
            send_json(stream, {"ok": False, "error": "Monitor exceeds 8192px texture limit"})
            return
        send_json(stream, {"ok": True, "codec": "jpeg444", "fps": args.fps})
        next_frame = 0.0
        # Capture only after the viewer has decoded the previous frame. Nothing stale queues.
        while read_exact(stream, 1) == b"N":
            time.sleep(max(0.0, next_frame - time.monotonic()))
            started = time.monotonic()
            width, height, data = encode_frame(capture, monitor, args.quality, args.max_width)
            if len(data) > MAX_FRAME:
                raise ValueError("Encoded frame exceeds protocol limit")
            stream.sendall(FRAME.pack(len(data), width, height) + data)
            next_frame = started + 1 / args.fps


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--bind", default="0.0.0.0")
    parser.add_argument("--port", type=int, default=47990)
    parser.add_argument("--private", type=Path, default=Path(__file__).parent / "private")
    parser.add_argument("--monitor", type=int, default=1)
    parser.add_argument("--fps", type=int, choices=range(1, 121), default=60, metavar="1..120")
    parser.add_argument("--quality", type=int, choices=range(1, 101), default=97, metavar="1..100")
    parser.add_argument("--max-width", type=int, default=0, help="0 preserves native resolution")
    args = parser.parse_args()
    if args.max_width < 0:
        parser.error("max-width must be >= 0")
    token = json.loads((args.private / "host.json").read_text(encoding="utf-8"))["token"]
    if not isinstance(token, str) or len(token) < 32:
        parser.error("Generate credentials with setup_host.py")
    context = ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER)
    context.minimum_version = ssl.TLSVersion.TLSv1_2
    context.load_cert_chain(args.private / "cert.pem", args.private / "key.pem")
    logging.basicConfig(level=logging.INFO, format="%(asctime)s %(message)s")
    busy = threading.Lock()

    def handle(raw, address):
        try:
            with raw, context.wrap_socket(raw, server_side=True) as stream:
                serve_viewer(stream, token, args)
        except (OSError, ValueError, EOFError) as error:
            logging.info("Viewer %s disconnected: %s", address[0], type(error).__name__)
        finally:
            busy.release()

    with socket.create_server((args.bind, args.port), backlog=4) as listener:
        logging.info("Desktop host listening on %s:%s; JPEG 4:4:4 Q%s, native=%s",
                     args.bind, args.port, args.quality, not args.max_width)
        try:
            while True:
                raw, address = listener.accept()
                if not busy.acquire(blocking=False):
                    raw.close()
                    continue
                raw.settimeout(10)
                raw.setsockopt(socket.IPPROTO_TCP, socket.TCP_NODELAY, 1)
                threading.Thread(target=handle, args=(raw, address), daemon=True).start()
        except KeyboardInterrupt:
            pass


if __name__ == "__main__":
    main()
