import hashlib
import io
import os
from pathlib import Path
import random
import struct
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch

ROOT=Path(__file__).resolve().parents[1]
sys.path.insert(0,str(ROOT/'pc-host'))
from video_protocol import AESGCM, HEADER, Receiver, packets
from video_host import access_units, is_keyframe, serve


class VideoTests(unittest.TestCase):
    def setUp(self):
        self.key=bytes(range(32));self.salt=b'QDS2'
        self.cipher=AESGCM(self.key)

    def encode(self,data,seq=0,w=3840,h=2160):
        return list(packets(self.cipher,self.salt,seq,w,h,True,seq*16667,data))

    def receive(self,ps):
        receiver=Receiver(self.key,self.salt)
        return [f for p in ps if (f:=receiver.feed(p,0)) is not None]

    def test_fec_each_group_reorder_duplicates_short_tail(self):
        rng=random.Random(7)
        for size in (1,1152-16,1152,9217,100000):
            data=rng.randbytes(size);ps=self.encode(data)
            count=HEADER.unpack_from(ps[0])[-2]
            # Lose the first data shard in every group, including partial last group.
            ps=[p for p in ps if not (HEADER.unpack_from(p)[-3]<count and HEADER.unpack_from(p)[-3]%8==0)]
            ps+=ps[:4];rng.shuffle(ps)
            frames=self.receive(ps)
            self.assertEqual(len(frames),1)
            self.assertEqual(frames[0][-1],data)

    def test_unrecoverable_loss_then_clean_keyframe(self):
        ps=self.encode(os.urandom(20000));ps=ps[2:]
        self.assertEqual(self.receive(ps),[])
        receiver=Receiver(self.key,self.salt)
        for p in ps: receiver.feed(p,0)
        decoded=[f for p in self.encode(b'recover',1) if (f:=receiver.feed(p,.2))]
        self.assertEqual(decoded[0][-1],b'recover')

    def test_tamper_wrong_key_metadata_and_replay(self):
        ps=self.encode(b'secret'*500)
        tampered=[p[:-1]+bytes([p[-1]^128]) for p in ps]
        self.assertEqual(self.receive(tampered),[])
        wrong=Receiver(bytes(32),self.salt)
        self.assertTrue(all(wrong.feed(p,0) is None for p in ps))
        modified=[]
        for p in ps:
            h=list(HEADER.unpack_from(p));h[2]=1920
            modified.append(HEADER.pack(*h)+p[HEADER.size:])
        self.assertEqual(self.receive(modified),[])
        self.assertEqual(len(self.receive(ps+ps)),1)

    def test_bounds_and_nonce_wrap(self):
        receiver=Receiver(self.key,self.salt)
        for p in (b'',b'QDV2',b'\0'*1400): self.assertIsNone(receiver.feed(p,0))
        for w,h in ((0,1),(9000,2),(8192,8192)):
            self.assertEqual(self.receive(self.encode(b'a',w=w,h=h)),[])
        with self.assertRaises(ValueError): self.encode(b'a',seq=0xffffffff)
        for seq in range(20):
            # Incomplete large frames cannot grow the assembly map without bound.
            receiver.feed(self.encode(bytes(3000),seq)[0],0)
        self.assertLessEqual(len(receiver.pending),8)

    def test_access_unit_parser_and_idr(self):
        for codec,aud,nal in (('hevc',b'\x46\x01\x50',b'\x26\x01'),('h264',b'\x09\xf0',b'\x65')):
            first=b'\0\0\0\1'+aud+b'\0\0\1'+nal+b'payload'
            second=b'\0\0\0\1'+aud+b'\0\0\1\x02\x01next'
            class ShortRead(io.BytesIO):
                def read(self,n):return super().read(min(n,3))
            self.assertEqual(list(access_units(ShortRead(first+second),codec)),[first,second])
            self.assertTrue(is_keyframe(first,codec))
            self.assertFalse(is_keyframe(second,codec))

    def test_qds2_rejects_auth_before_encoder_or_udp(self):
        for auth in ({'protocol':'QDS2','token':'wrong'}, {'protocol':'QDS1','token':'valid'},
                     {'protocol':'QDS2','token':[]}, {}):
            with (patch('video_host.receive_json',return_value=auth), patch('video_host.send_json') as reply,
                  patch('video_host.subprocess.Popen') as encoder):
                serve(None,('127.0.0.1',1),None,'valid',None)
                self.assertFalse(reply.call_args.args[1]['ok'])
                encoder.assert_not_called()

    def test_android_java_receiver_matches_python(self):
        java=Path(os.environ.get('JAVA_HOME',r'C:\Program Files\Unity\Hub\Editor\6000.3.28f1\Editor\Data\PlaybackEngines\AndroidPlayer\OpenJDK'))/'bin'
        self.assertTrue((java/'javac.exe').exists(),'JDK required for production receiver test')
        rng=random.Random(123);ps=[];expected=[]
        for seq,(w,h) in enumerate(((3840,2160),(2560,1600),(1080,1920),(3440,1440),(1920,1080))):
            data=rng.randbytes(40000+seq*100)
            expected.append(f'{seq} {w} {h} {hashlib.sha256(data).hexdigest()}')
            frame=self.encode(data,seq,w,h);count=HEADER.unpack_from(frame[0])[-2]
            frame=[p for p in frame if not (HEADER.unpack_from(p)[-3]<count and HEADER.unpack_from(p)[-3]%8==2)]
            rng.shuffle(frame);ps.extend(frame+frame[:3])
        # Authentication corruption must not produce a sixth frame.
        ps.extend(p[:HEADER.size]+bytes([p[HEADER.size]^1])+p[HEADER.size+1:] for p in self.encode(b'bad',seq=5))
        with tempfile.TemporaryDirectory(prefix='qds2-java-') as directory:
            source=ROOT/'QuestDesktop/Assets/Plugins/Android/com/miandic/questdesktop/VideoPackets.java'
            subprocess.run([str(java/'javac.exe'),'-d',directory,str(source),str(ROOT/'tests/VideoPacketsHarness.java')],check=True,capture_output=True)
            fixture=Path(directory)/'packets.bin'
            fixture.write_bytes(self.key+self.salt+struct.pack('!I',len(ps))+b''.join(struct.pack('!I',len(p))+p for p in ps))
            result=subprocess.run([str(java/'java.exe'),'-cp',directory,'VideoPacketsHarness',str(fixture)],check=True,capture_output=True,text=True)
            self.assertEqual(result.stdout.strip().splitlines(),expected)


if __name__=='__main__':unittest.main()
