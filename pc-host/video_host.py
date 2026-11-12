"""GPU desktop host: D3D11 Desktop Duplication → NVENC → encrypted UDP/FEC.

No pixel readback, JPEG or per-frame acknowledgement. Requires FFmpeg with
ddagrab/NVENC. Credentials are shared with the legacy host; only one may bind.
"""
import argparse
import base64
import hashlib
import hmac
import json
import logging
import os
from pathlib import Path
import re
import socket
import ssl
import subprocess
import threading
import time
from cryptography.hazmat.primitives.ciphers.aead import AESGCM
from protocol import receive_json, send_json
from video_protocol import packets
from video_bitstream import dimensions as coded_dimensions


def dimensions(args):
    if args.test_source:
        modes = args.test_source.split(',')
        elapsed = time.monotonic() - getattr(args, 'test_started', time.monotonic())
        mode = modes[int(elapsed / args.mode_seconds) % len(modes)]
        return tuple(map(int, mode.split('x')))
    import mss
    with getattr(mss, 'MSS', mss.mss)() as capture:
        monitors = capture.monitors
        if not 0 <= args.output < len(monitors) - 1:
            raise ValueError('Display unavailable')
        m = monitors[args.output + 1]
        return m['width'], m['height']


def encoder_command(args, codec, fps, bitrate, size=None):
    cmd = [args.ffmpeg, '-hide_banner', '-loglevel', 'warning', '-nostdin']
    if args.test_source:
        w, h = size or dimensions(args)
        cmd += ['-readrate', '1', '-readrate_initial_burst', '0', '-readrate_catchup', '1',
                '-f', 'lavfi', '-i', f'testsrc2=size={w}x{h}:rate={fps}']
    else:
        cmd += ['-f', 'lavfi', '-i', f'ddagrab=output_idx={args.output}:framerate={fps}:draw_mouse=1']
    # ddagrab timestamps can round to the same 1/fps encoder tick. Assign distinct
    # frame PTS without touching GPU pixels; otherwise the raw muxer drops frames.
    cmd += ['-vf', f'setpts=N/({fps}*TB)', '-an', '-c:v', 'hevc_nvenc' if codec == 'hevc' else 'h264_nvenc',
            '-preset', 'p4', '-tune', 'ull', '-rc', 'cbr', '-b:v', str(bitrate),
            '-maxrate', str(bitrate), '-bufsize', str(bitrate // fps), '-bf', '0',
            '-rc-lookahead', '0', '-zerolatency', '1', '-delay', '0', '-g', str(max(1, fps // 2)),
            '-forced-idr', '1', '-aud', '1', '-color_range', 'tv', '-colorspace', 'bt709',
            '-color_trc', 'bt709', '-color_primaries', 'bt709',
            '-fps_mode', 'passthrough', '-flush_packets', '1', '-f', 'hevc' if codec == 'hevc' else 'h264', 'pipe:1']
    return cmd


def access_units(pipe, codec):
    """Split encoder Annex-B at AUD boundaries; keep parameter sets with their IDR.

    Four-byte AUD prefix from NVENC. Also accepts legal three-byte start codes.
    One following AUD is needed to delimit an AU (at most one frame of latency).
    """
    buffer = bytearray()
    pattern = re.compile(b'\x00\x00(?:\x00)?\x01' + (b'\x46' if codec == 'hevc' else b'\x09'))
    while True:
        chunk = pipe.read(65536)
        if not chunk:
            if buffer:
                yield bytes(buffer)
            return
        buffer.extend(chunk)
        starts = list(pattern.finditer(buffer))
        if len(starts) >= 2:
            end = 0
            for match in starts[1:]:
                yield bytes(buffer[end:match.start()])
                end = match.start()
            del buffer[:end]
        if len(buffer) > 4 * 1024 * 1024:
            raise ValueError('Encoder AU overflow or missing AUD')


def is_keyframe(data, codec):
    for match in re.finditer(b'\x00\x00(?:\x00)?\x01(.)', data, re.DOTALL):
        value = match.group(1)[0]
        if (codec == 'hevc' and 16 <= (value >> 1) & 63 <= 21) or (codec == 'h264' and value & 31 == 5):
            return True
    return False


def serve(stream, peer, udp, token, args):
    auth = receive_json(stream)
    if (auth.get('protocol') != 'QDS2' or not isinstance(auth.get('token'), str)
            or not hmac.compare_digest(auth['token'].encode(), token.encode())):
        send_json(stream, {'ok': False, 'error': 'Authentication failed'})
        return
    codec = auth.get('codec', 'hevc')
    if codec not in ('hevc', 'h264'):
        raise ValueError('Unsupported codec')
    fps = min(args.fps, max(30, min(60, int(auth.get('fps', 60)))))
    bitrate = min(args.bitrate * 1000000, max(5000000, min(65000000, int(auth.get('bitrate', 50000000)))))
    key, salt = os.urandom(32), os.urandom(4)
    cipher = AESGCM(key)
    width, height = dimensions(args)
    def check_size(w, h):
        if not (2 <= w <= 8192 and 2 <= h <= 8192 and w * h <= 33554432 and w % 2 == h % 2 == 0):
            raise ValueError('NVENC 4:2:0 needs even dimensions, 2..8192, <=32MP')
    check_size(width, height)
    send_json(stream, {'ok': True, 'codec': codec, 'fps': fps, 'bitrate': bitrate,
                      'width': width, 'height': height, 'udpPort': args.udp_port,
                      'key': base64.b64encode(key).decode(), 'salt': base64.b64encode(salt).decode()})
    hello = hmac.digest(key, b'QDS2 hello', 'sha256')
    udp.settimeout(.5)
    deadline = time.monotonic() + 8
    address = None
    while time.monotonic() < deadline:
        try:
            data, addr = udp.recvfrom(2048)
            if addr[0] == peer[0] and hmac.compare_digest(data, hello):
                address = addr
                break
        except socket.timeout:
            pass
    if address is None:
        raise TimeoutError('Authenticated UDP probe missing')
    stop = threading.Event()
    process = [None]
    def heartbeat():
        try:
            stream.settimeout(5)
            while not stop.is_set():
                report = receive_json(stream)
                if report.get('stop'):
                    break
        except (OSError, ValueError, EOFError):
            pass
        finally:
            stop.set()
            if process[0] is not None:
                try: process[0].terminate()
                except OSError: pass
    threading.Thread(target=heartbeat, daemon=True).start()
    sequence = 0
    started = time.monotonic()
    try:
        while not stop.is_set():
            width, height = dimensions(args)
            source_size = (width, height)
            check_size(width, height)
            logging.info('NVENC %s %sx%s @ %s, %.0f Mbps, output %s', codec, width, height, fps, bitrate / 1e6, args.output)
            with subprocess.Popen(encoder_command(args, codec, fps, bitrate, source_size), stdout=subprocess.PIPE,
                                  stderr=subprocess.PIPE, bufsize=0, creationflags=subprocess.CREATE_NO_WINDOW) as proc:
                process[0] = proc
                def errors():
                    for line in proc.stderr:
                        logging.warning('FFmpeg: %s', line.decode(errors='replace').strip())
                log_thread = threading.Thread(target=errors, daemon=True)
                log_thread.start()
                next_size_check = time.monotonic() + 1
                mode_change = False
                try:
                    for data in access_units(proc.stdout, codec):
                        if stop.is_set(): break
                        keyframe = is_keyframe(data, codec)
                        size = coded_dimensions(data, codec) if keyframe else None
                        if size is not None:
                            width, height = size
                            check_size(width, height)
                        pts = int((time.monotonic() - started) * 1e6)
                        for packet in packets(cipher, salt, sequence, width, height, keyframe, pts, data):
                            udp.sendto(packet, address)
                        sequence += 1
                        if time.monotonic() >= next_size_check:
                            next_size_check = time.monotonic() + 1
                            if dimensions(args) != source_size:
                                mode_change = True
                                break  # restart encoder at the new native mode; first AU is IDR
                finally:
                    if proc.poll() is None: proc.terminate()
                    proc.wait(timeout=5)
                    log_thread.join(timeout=1)
                    process[0] = None
                if proc.returncode and not stop.is_set() and not mode_change:
                    if not args.test_source and dimensions(args) != source_size:
                        continue  # DXGI reports OUTPUT_CHANGED before the periodic check.
                    raise RuntimeError('NVENC exited; see FFmpeg diagnostic above')
    finally:
        stop.set()
        try: stream.shutdown(socket.SHUT_RDWR)
        except OSError: pass


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--bind', default='0.0.0.0')
    parser.add_argument('--port', type=int, default=47990)
    parser.add_argument('--udp-port', type=int, default=47991)
    parser.add_argument('--private', type=Path, default=Path(__file__).parent / 'private')
    parser.add_argument('--ffmpeg', default='ffmpeg')
    parser.add_argument('--output', type=int, default=0, help='DXGI display index (zero based)')
    parser.add_argument('--fps', type=int, choices=(30, 60), default=60)
    parser.add_argument('--bitrate', type=int, default=50, help='Mbps before FEC (5..65)')
    parser.add_argument('--test-source', help='Moving synthetic source, e.g. 3840x2160; no desktop capture')
    parser.add_argument('--mode-seconds', type=float, default=5, help='Synthetic comma-separated modes switch interval')
    args = parser.parse_args()
    if args.mode_seconds < 1: parser.error('mode-seconds must be >= 1')
    args.test_started = time.monotonic()
    if not 5 <= args.bitrate <= 65: parser.error('bitrate must be 5..65 Mbps')
    token = json.loads((args.private / 'host.json').read_text())['token']
    if not isinstance(token, str) or len(token) < 32: parser.error('Generate credentials with setup_host.py')
    context = ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER)
    context.minimum_version = ssl.TLSVersion.TLSv1_2
    context.load_cert_chain(args.private / 'cert.pem', args.private / 'key.pem')
    logging.basicConfig(level=logging.INFO, format='%(asctime)s %(message)s')
    with socket.create_server((args.bind, args.port)) as listener, socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as udp:
        udp.setsockopt(socket.SOL_SOCKET, socket.SO_SNDBUF, 4 * 1024 * 1024)
        udp.bind((args.bind, args.udp_port))
        logging.info('QDS2 TLS %s:%s, UDP %s; native GPU capture', args.bind, args.port, args.udp_port)
        while True:
            raw, peer = listener.accept()
            try:
                raw.settimeout(10)
                raw.setsockopt(socket.IPPROTO_TCP, socket.TCP_NODELAY, 1)
                with raw, context.wrap_socket(raw, server_side=True) as stream:
                    serve(stream, peer, udp, token, args)
            except (OSError, ValueError, EOFError, RuntimeError) as error:
                logging.warning('Session ended: %s', error)


if __name__ == '__main__':
    main()
