"""QDS1: TLS, length-prefixed JSON auth, then pull-based independent JPEG frames."""
import json
import struct

MAX_JSON = 4096
MAX_FRAME = 32 * 1024 * 1024
FRAME = struct.Struct("!III")  # payload length, width, height


def read_exact(stream, size):
    data = bytearray()
    while len(data) < size:
        chunk = stream.recv(size - len(data))
        if not chunk:
            raise EOFError("Peer disconnected")
        data.extend(chunk)
    return bytes(data)


def receive_json(stream):
    size = struct.unpack("!I", read_exact(stream, 4))[0]
    if not 0 < size <= MAX_JSON:
        raise ValueError("Invalid JSON length")
    value = json.loads(read_exact(stream, size))
    if not isinstance(value, dict):
        raise ValueError("Expected object")
    return value


def send_json(stream, value):
    data = json.dumps(value).encode("utf-8")
    if len(data) > MAX_JSON:
        raise ValueError("JSON too large")
    stream.sendall(struct.pack("!I", len(data)) + data)
