"""Read coded display dimensions from SPS, rather than assuming monitor enumeration order.

This also handles cropped/padded coded sizes. Encoder remains responsible for
supported even 4:2:0 input sizes; VR window aspect never changes capture resolution.
"""
import re


class Bits:
    def __init__(self, data):
        self.data = data
        self.pos = 0

    def take(self, n=1):
        if self.pos + n > len(self.data) * 8:
            raise ValueError('Truncated SPS')
        value = 0
        for _ in range(n):
            value = (value << 1) | ((self.data[self.pos // 8] >> (7 - self.pos % 8)) & 1)
            self.pos += 1
        return value

    def ue(self):
        zeros = 0
        while not self.take():
            zeros += 1
            if zeros > 31: raise ValueError('SPS Exp-Golomb overflow')
        return (1 << zeros) - 1 + self.take(zeros)

    def se(self):
        n = self.ue()
        return (n + 1) // 2 if n & 1 else -(n // 2)


def dimensions(data, codec):
    for nal in re.split(b'\x00\x00(?:\x00)?\x01', data)[1:]:
        if not nal: continue
        kind = (nal[0] >> 1) & 63 if codec == 'hevc' else nal[0] & 31
        if kind != (33 if codec == 'hevc' else 7): continue
        rbsp = nal[2 if codec == 'hevc' else 1:].replace(b'\0\0\3', b'\0\0')
        b = Bits(rbsp)
        if codec == 'hevc':
            b.take(4); layers = b.take(3); b.take()
            b.take(96)  # general profile_tier_level
            flags = [(b.take(), b.take()) for _ in range(layers)]
            if layers: b.take(2 * (8 - layers))
            for profile, level in flags:
                if profile: b.take(88)
                if level: b.take(8)
            b.ue(); chroma = b.ue()
            separate = b.take() if chroma == 3 else 0
            w, h = b.ue(), b.ue()
            if b.take():
                left, right, top, bottom = [b.ue() for _ in range(4)]
                subw = 2 if chroma in (1, 2) and not separate else 1
                subh = 2 if chroma == 1 and not separate else 1
                w -= subw * (left + right); h -= subh * (top + bottom)
        else:
            profile = b.take(8); b.take(16); b.ue(); chroma = 1; separate = 0
            if profile in (100, 110, 122, 244, 44, 83, 86, 118, 128, 138, 139, 134, 135):
                chroma = b.ue()
                if chroma == 3: separate = b.take()
                b.ue(); b.ue(); b.take()
                if b.take():
                    for i in range(8 if chroma != 3 else 12):
                        if b.take():
                            last = next_scale = 8
                            for _ in range(16 if i < 6 else 64):
                                if next_scale: next_scale = (last + b.se() + 256) % 256
                                last = next_scale or last
            b.ue(); order = b.ue()
            if order == 0: b.ue()
            elif order == 1:
                b.take(); b.se(); b.se()
                for _ in range(b.ue()): b.se()
            b.ue(); b.take(); w = 16 * (b.ue() + 1); map_height = b.ue() + 1
            frame_only = b.take(); h = (2 - frame_only) * map_height * 16
            if not frame_only: b.take()
            b.take()
            if b.take():
                left, right, top, bottom = [b.ue() for _ in range(4)]
                chroma = 0 if separate else chroma
                subw = 2 if chroma in (1, 2) else 1
                subh = 2 if chroma == 1 else 1
                w -= subw * (left + right); h -= subh * (2 - frame_only) * (top + bottom)
        if not 1 <= w <= 8192 or not 1 <= h <= 8192:
            raise ValueError('SPS dimensions out of bounds')
        return w, h
    return None
