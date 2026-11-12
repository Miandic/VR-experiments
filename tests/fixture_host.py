"""One-shot loopback TLS fixture used by the Unity integration check."""
import json
from pathlib import Path
import socket
import ssl
import sys
from unittest.mock import patch

from test_stream import FakeCapture
from setup_host import provision
from host import serve_viewer
import argparse

directory = Path(sys.argv[1])
config = provision(directory / "keys", "127.0.0.1", "", 47990)
context = ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER)
context.load_cert_chain(directory / "keys/cert.pem", directory / "keys/key.pem")
with socket.create_server(("127.0.0.1", 0)) as listener:
    listener.settimeout(20)
    config["port"] = listener.getsockname()[1]
    (directory / "connection.json").write_text(json.dumps(config), encoding="utf-8")
    raw, _ = listener.accept()
    try:
        with raw, context.wrap_socket(raw, server_side=True) as stream:
            stream.settimeout(5)
            with patch("mss.mss", return_value=FakeCapture()):
                serve_viewer(stream, config["token"], argparse.Namespace(
                    monitor=1, quality=97, fps=60, max_width=0))
    except (OSError, EOFError):
        pass  # Viewer closes after successful decode or certificate rejection.
