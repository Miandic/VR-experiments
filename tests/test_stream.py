import argparse
import hashlib
import io
import socket
import ssl
import sys
import tempfile
import threading
import unittest
from pathlib import Path
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "pc-host"))
from protocol import FRAME, read_exact, receive_json, send_json
from setup_host import provision
from host import encode_frame, serve_viewer
from PIL import Image


class FragmentedSocket:
    def __init__(self, data):
        self.data = bytearray(data)

    def recv(self, size):
        result = self.data[:min(size, 2)]
        del self.data[:len(result)]
        return bytes(result)


class FakeCapture:
    monitors = [{}, {"width": 64, "height": 32}]

    def __enter__(self): return self
    def __exit__(self, *args): pass

    def grab(self, monitor):
        return argparse.Namespace(size=(64, 32), bgra=bytes([10, 80, 230, 255]) * (64 * 32))


class StreamingTests(unittest.TestCase):
    def test_fragmentation_and_eof(self):
        self.assertEqual(read_exact(FragmentedSocket(b"abcdef"), 6), b"abcdef")
        with self.assertRaises(EOFError): read_exact(FragmentedSocket(b"a"), 2)

    def test_json_bounds(self):
        for invalid in (b"\0\0\0\0", b"\xff\xff\xff\xff", b"\0\0\0\x02[]"):
            with self.assertRaises(ValueError): receive_json(FragmentedSocket(invalid))

    def test_native_resolution_and_no_chroma_subsampling(self):
        w, h, data = encode_frame(FakeCapture(), {}, 97, 0)
        from PIL.JpegImagePlugin import get_sampling
        image = Image.open(io.BytesIO(data))
        self.assertEqual((w, h), (64, 32))
        self.assertEqual(get_sampling(image), 0)
        self.assertEqual(image.size, (64, 32))
        self.assertEqual(encode_frame(FakeCapture(), {}, 97, 16)[:2], (16, 8))

    def tls_session(self, correct_token=True):
        with tempfile.TemporaryDirectory() as directory:
            config = provision(directory, "127.0.0.1", "", 47990)
            with self.assertRaises(ValueError): provision(directory, "127.0.0.1", "", 47990)
            context = ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER)
            context.load_cert_chain(Path(directory) / "cert.pem", Path(directory) / "key.pem")
            errors = []
            with socket.create_server(("127.0.0.1", 0)) as listener:
                def server():
                    try:
                        raw, _ = listener.accept()
                        with raw, context.wrap_socket(raw, server_side=True) as stream:
                            stream.settimeout(2)
                            serve_viewer(stream, config["token"], argparse.Namespace(
                                monitor=1, quality=97, fps=60, max_width=0))
                    except Exception as error: errors.append(error)

                with patch("mss.mss", return_value=FakeCapture()):
                    thread = threading.Thread(target=server, daemon=True); thread.start()
                    client = ssl.SSLContext(ssl.PROTOCOL_TLS_CLIENT)
                    client.check_hostname = False
                    client.verify_mode = ssl.CERT_NONE  # Followed by mandatory explicit pin check, as on Quest.
                    with socket.create_connection(listener.getsockname()) as raw:
                        with client.wrap_socket(raw, server_hostname="127.0.0.1") as stream:
                            stream.settimeout(2)
                            self.assertEqual(hashlib.sha256(stream.getpeercert(binary_form=True)).hexdigest(),
                                             config["certificateSha256"])
                            send_json(stream, {"protocol": "QDS1", "token": config["token"] if correct_token else "wrong"})
                            reply = receive_json(stream)
                            self.assertEqual(reply["ok"], correct_token)
                            if correct_token:
                                # No unsolicited frames: data is pulled one frame at a time.
                                for _ in range(3):
                                    stream.sendall(b"N")
                                    size, w, h = FRAME.unpack(read_exact(stream, FRAME.size))
                                    image = Image.open(io.BytesIO(read_exact(stream, size)))
                                    self.assertEqual(image.size, (w, h))
                                    self.assertEqual((w, h), (64, 32))
                                stream.sendall(b"Q")
                    thread.join(3)
                    self.assertFalse(thread.is_alive())
            self.assertEqual(errors, [])

    def test_tls_frames_and_provisioning(self): self.tls_session()
    def test_wrong_token_rejected(self): self.tls_session(False)


if __name__ == "__main__": unittest.main()
