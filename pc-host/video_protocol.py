"""QDS2 media: authenticated access units, MTU-safe UDP, XOR FEC per 8 shards.

TLS carries credentials and fresh AES-256 key/nonce prefix for every session.
Sequence numbers never wrap within a session. Header metadata is authenticated
as GCM associated data; reconstructed ciphertext is authenticated before decode.
"""
import math
import struct
from cryptography.hazmat.primitives.ciphers.aead import AESGCM

MAGIC = b'QDV2'
CHUNK = 1152
GROUP = 8
MAX_FRAME = 4 * 1024 * 1024
HEADER = struct.Struct('!4sIHHBQHHI')
META = struct.Struct('!IHHBQ')


def packets(cipher, salt, sequence, width, height, keyframe, pts, data):
    if not 0 <= sequence < 0xffffffff:
        raise ValueError('Start a new session before sequence wrap')
    flags = int(keyframe)
    meta = META.pack(sequence, width, height, flags, pts)
    encrypted = cipher.encrypt(salt + sequence.to_bytes(8, 'big'), data, meta)
    if not 0 < len(encrypted) <= MAX_FRAME:
        raise ValueError('Access unit exceeds bound')
    count = math.ceil(len(encrypted) / CHUNK)
    for start in range(0, count, GROUP):
        parity = 0
        for index in range(start, min(start + GROUP, count)):
            shard = encrypted[index * CHUNK:(index + 1) * CHUNK]
            parity ^= int.from_bytes(shard.ljust(CHUNK, b'\0'), 'little')
            yield HEADER.pack(MAGIC, sequence, width, height, flags, pts,
                              index, count, len(encrypted)) + shard
        yield HEADER.pack(MAGIC, sequence, width, height, flags, pts,
                          count + start // GROUP, count, len(encrypted)) + parity.to_bytes(CHUNK, 'little')


class Assembly:
    def __init__(self, header, now):
        _, self.seq, self.width, self.height, self.flags, self.pts, _, self.count, self.total = header
        self.meta = META.pack(self.seq, self.width, self.height, self.flags, self.pts)
        self.created = now
        self.shards = {}
        self.present = 0

    def add(self, index, data):
        if index not in self.shards:
            self.shards[index] = data
            if index < self.count:
                self.present += 1
        for start in [index // GROUP * GROUP if index < self.count else (index - self.count) * GROUP]:
            missing = [i for i in range(start, min(start + GROUP, self.count)) if i not in self.shards]
            parity = self.shards.get(self.count + start // GROUP)
            if len(missing) == 1 and parity is not None:
                recovered = bytearray(parity)
                for i in range(start, min(start + GROUP, self.count)):
                    if i == missing[0]:
                        continue
                    for j, value in enumerate(self.shards[i]):
                        recovered[j] ^= value
                size = min(CHUNK, self.total - missing[0] * CHUNK)
                self.shards[missing[0]] = bytes(recovered[:size])
                self.present += 1
        if self.present == self.count:
            return b''.join(self.shards[i] for i in range(self.count))


class Receiver:
    """Reference receiver for the simulator; bounded incomplete frames and replay floor."""
    def __init__(self, key, salt):
        self.cipher, self.salt = AESGCM(key), salt
        self.pending, self.done = {}, set()
        self.highest = -1

    def feed(self, packet, now):
        if len(packet) < HEADER.size:
            return None
        h = HEADER.unpack_from(packet)
        magic, seq, width, height, flags, pts, index, count, total = h
        if (magic != MAGIC or not 1 <= width <= 8192 or not 1 <= height <= 8192
                or width * height > 33554432 or flags > 1 or not 16 <= total <= MAX_FRAME
                or count != math.ceil(total / CHUNK) or index >= count + math.ceil(count / GROUP)):
            return None
        expected = CHUNK if index >= count else min(CHUNK, total - index * CHUNK)
        if len(packet) != HEADER.size + expected or seq in self.done or seq <= self.highest - 8:
            return None
        self.pending = {s: a for s, a in self.pending.items() if now - a.created <= .1}
        if seq not in self.pending:
            if len(self.pending) >= 8:
                del self.pending[min(self.pending)]
            self.pending[seq] = Assembly(h, now)
        a = self.pending[seq]
        if (width, height, flags, pts, count, total) != (a.width, a.height, a.flags, a.pts, a.count, a.total):
            return None
        encrypted = a.add(index, packet[HEADER.size:])
        if encrypted is None:
            return None
        del self.pending[seq]
        try:
            data = self.cipher.decrypt(self.salt + seq.to_bytes(8, 'big'), encrypted, a.meta)
        except Exception:
            return None
        self.highest = max(self.highest, seq)
        self.done.add(seq)
        self.done = {s for s in self.done if s > self.highest - 8}
        return (seq, width, height, bool(flags), pts, data)
