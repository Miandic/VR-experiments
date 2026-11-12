"""Run a real NVENC → TLS/UDP/FEC → NVDEC loopback session without a headset.

Tests transport and PC codec throughput, NOT Quest MediaCodec/compositor or Wi-Fi.
Host and decoder are independent processes. No captured images are saved.
"""
import argparse
import base64
import hashlib
import hmac
import json
from pathlib import Path
import queue
import random
import socket
import ssl
import statistics
import subprocess
import sys
import tempfile
import threading
import time

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / 'pc-host'))
from setup_host import provision
from protocol import receive_json, send_json
from video_protocol import Receiver


def port(kind=socket.SOCK_STREAM):
    with socket.socket(type=kind) as s:
        s.bind(('127.0.0.1', 0))
        return s.getsockname()[1]


def run(args):
    control_port, udp_port = port(), port(socket.SOCK_DGRAM)
    with tempfile.TemporaryDirectory(prefix='qds2-sim-') as directory:
        private = Path(directory) / 'private'
        config = provision(private, '127.0.0.1', '', control_port)
        command = [sys.executable, str(ROOT / 'pc-host/video_host.py'), '--bind', '127.0.0.1',
                   '--port', str(control_port), '--udp-port', str(udp_port), '--private', str(private),
                   '--ffmpeg', args.ffmpeg, '--fps', str(args.fps), '--bitrate', str(args.bitrate)]
        if args.resolution != 'desktop': command += ['--test-source', args.resolution]
        hostlog = open(Path(directory) / 'host.log', 'w+')
        host = subprocess.Popen(command, stdout=hostlog, stderr=hostlog, creationflags=subprocess.CREATE_NO_WINDOW)
        decoder = None
        decode_log = None
        try:
            deadline = time.monotonic() + 15
            while True:
                try:
                    raw = socket.create_connection(('127.0.0.1', control_port), timeout=1)
                    break
                except OSError:
                    if host.poll() is not None or time.monotonic() > deadline: raise RuntimeError('Host did not start')
                    time.sleep(.05)
            context = ssl.SSLContext(ssl.PROTOCOL_TLS_CLIENT)
            context.check_hostname = False
            context.verify_mode = ssl.CERT_NONE
            with raw, context.wrap_socket(raw, server_hostname='localhost') as tls, socket.socket(type=socket.SOCK_DGRAM) as udp:
                assert hashlib.sha256(tls.getpeercert(True)).hexdigest() == config['certificateSha256']
                tls.settimeout(10)
                send_json(tls, dict(protocol='QDS2', token=config['token'], codec=args.codec, fps=args.fps, bitrate=args.bitrate * 1000000))
                reply = receive_json(tls)
                if not reply.get('ok'): raise RuntimeError(reply)
                key, salt = base64.b64decode(reply['key']), base64.b64decode(reply['salt'])
                receiver = Receiver(key, salt)
                udp.setsockopt(socket.SOL_SOCKET, socket.SO_RCVBUF, 4 * 1024 * 1024)
                udp.connect(('127.0.0.1', reply['udpPort']))
                udp.settimeout(.004)
                hello = hmac.digest(key, b'QDS2 hello', 'sha256')
                udp.send(hello)
                decode_log = open(Path(directory) / 'decode.log', 'w+')
                decoder = subprocess.Popen([args.ffmpeg, '-hide_banner', '-loglevel', 'warning',
                    '-probesize', '32', '-analyzeduration', '0', '-hwaccel', 'cuda', '-hwaccel_output_format', 'cuda',
                    '-f', args.codec, '-i', 'pipe:0', '-an', '-fps_mode', 'passthrough', '-progress', 'pipe:1',
                    '-stats_period', '0.5', '-f', 'null', '-'], stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                    stderr=decode_log, bufsize=0, creationflags=subprocess.CREATE_NO_WINDOW)
                decode_frames = [0]
                decode_samples = []
                def progress():
                    for line in decoder.stdout:
                        if line.startswith(b'frame='):
                            decode_frames[0] = int(line.split(b'=')[1])
                            decode_samples.append((time.monotonic(), decode_frames[0]))
                reader = threading.Thread(target=progress); reader.start()
                work = queue.Queue(maxsize=3)
                writer_errors = []
                def write():
                    try:
                        while True:
                            data = work.get()
                            if data is None: break
                            view = memoryview(data)
                            while view:
                                n = decoder.stdin.write(view)
                                if not n: raise IOError('Decoder pipe closed')
                                view = view[n:]
                    except Exception as e: writer_errors.append(str(e))
                    finally: decoder.stdin.close()
                writer = threading.Thread(target=write); writer.start()
                started = time.monotonic(); first = None; next_report = started; last = started
                received = total_bytes = lost = dropped = injected = 0
                arrivals = []; steady = []; ready = {}; expected = -1; gap_since = None; waiting_key = True
                observed_sizes = set(); warmup_drops = 0
                rng = random.Random(42)
                while time.monotonic() - started < args.seconds:
                    now = time.monotonic()
                    try:
                        packet = udp.recv(2048)
                        if rng.random() < args.loss: injected += 1; continue
                        frame = receiver.feed(packet, now)
                        if frame is not None:
                            if first is None: first = now
                            received += 1; total_bytes += len(frame[5]); arrivals.append(now - last); last = now
                            observed_sizes.add((frame[1], frame[2]))
                            if now - first >= 2: steady.append(now)
                            if frame[0] >= expected: ready[frame[0]] = frame
                    except socket.timeout: pass
                    while ready:
                        sequence = min(ready); frame = ready[sequence]
                        if expected >= 0 and sequence != expected and not frame[3]:
                            if gap_since is None: gap_since = now
                            if now - gap_since < .012 and len(ready) < 4: break
                            lost += sequence - expected; waiting_key = True
                        del ready[sequence]; expected = sequence + 1; gap_since = None
                        if waiting_key and not frame[3]: dropped += 1; continue
                        if frame[3]: waiting_key = False
                        try: work.put_nowait(frame[5])
                        except queue.Full:
                            dropped += 1; waiting_key = True
                    if now >= next_report:
                        send_json(tls, dict(received=received, decoded=decode_frames[0]))
                        if received == 0: udp.send(hello)
                        next_report = now + 1
                    if writer_errors: raise RuntimeError(writer_errors[0])
                    if first is None or now - first < 2: warmup_drops = dropped
                send_json(tls, {'stop': True})
                work.put(None, timeout=5); writer.join(10)
                if writer.is_alive(): raise RuntimeError('Decoder drain stalled')
                decoder.wait(timeout=10); reader.join(5)
                decode_log.seek(0); errors = decode_log.read(); decode_log.close()
                if decoder.returncode: raise RuntimeError('NVDEC failed: ' + errors)
                duration = max(.001, last - (first or started))
                samples = [s for s in decode_samples if first and s[0] > first + 2 and s[0] < last]
                result = dict(resolution=[reply['width'], reply['height']], codec=args.codec,
                    requested_fps=args.fps, seconds=args.seconds, received_frames=received,
                    decoded_frames=decode_frames[0], receive_fps=(received - 1) / duration,
                    steady_receive_fps=(len(steady) - 1)/(steady[-1]-steady[0]) if len(steady)>1 else 0,
                    steady_decode_fps=(samples[-1][1]-samples[0][1])/(samples[-1][0]-samples[0][0]) if len(samples)>1 else 0,
                    frame_interval_p50_ms=statistics.median(arrivals[1:])*1000 if received>1 else 0,
                    frame_interval_p95_ms=sorted(arrivals[1:])[int((received-2)*.95)]*1000 if received>1 else 0,
                    encoded_mbps=total_bytes*8/duration/1e6, lost_frames=lost, dependency_or_queue_drops=dropped,
                    injected_packet_drops=injected, decoder_warnings=errors.strip(),
                    observed_resolutions=sorted(observed_sizes), startup_drops=warmup_drops,
                    steady_dependency_or_queue_drops=dropped-warmup_drops,
                    scope='PC loopback NVENC + encrypted UDP/FEC + NVDEC; not Quest/Wi-Fi/end-to-end display latency')
                if args.output:
                    Path(args.output).write_text(json.dumps(result, indent=2), encoding='utf-8')
                print(json.dumps(result, indent=2))
                if received < 2 or decode_frames[0] < 2: raise RuntimeError('No usable video')
                return result
        finally:
            # Only the host process tree created by this simulator. Do not orphan its
            # encoder if a failed test exits before the control heartbeat handles EOF.
            if host.poll() is None:
                subprocess.run(['taskkill', '/PID', str(host.pid), '/T', '/F'],
                               stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL,
                               creationflags=subprocess.CREATE_NO_WINDOW)
            host.wait(timeout=5)
            if decoder is not None and decoder.poll() is None: decoder.kill(); decoder.wait()
            if decode_log is not None and not decode_log.closed:
                decode_log.seek(0); print(decode_log.read(), file=sys.stderr); decode_log.close()
            hostlog.seek(0)
            print(hostlog.read(), file=sys.stderr)
            hostlog.close()


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--ffmpeg', required=True)
    parser.add_argument('--resolution', default='3840x2160', help='even WIDTHxHEIGHT or desktop')
    parser.add_argument('--codec', choices=('hevc', 'h264'), default='hevc')
    parser.add_argument('--fps', type=int, choices=(30, 60), default=60)
    parser.add_argument('--bitrate', type=int, default=50)
    parser.add_argument('--seconds', type=int, default=30)
    parser.add_argument('--loss', type=float, default=0)
    parser.add_argument('--output')
    run(parser.parse_args())
