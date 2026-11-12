package com.miandic.questdesktop;

import java.nio.ByteBuffer;
import java.util.*;
import javax.crypto.Cipher;
import javax.crypto.spec.GCMParameterSpec;
import javax.crypto.spec.SecretKeySpec;

/** Pure Java QDS2 receiver, also compiled/run by desktop fault-injection tests. */
public final class VideoPackets {
    public static final int CHUNK = 1152, GROUP = 8, HEADER = 29, MAX = 4 * 1024 * 1024;
    public static final class Frame {
        public long sequence, pts;
        public int width, height;
        public boolean key;
        public byte[] data;
    }
    private static final class Assembly {
        long seq, pts, created;
        int width, height, flags, count, total, present;
        byte[][] shards;
        Assembly(long s, int w, int h, int f, long p, int c, int t, long now) {
            seq=s; width=w; height=h; flags=f; pts=p; count=c; total=t; created=now;
            shards=new byte[c+(c+7)/8][];
        }
    }
    private final SecretKeySpec key;
    private final byte[] salt;
    private final TreeMap<Long,Assembly> pending = new TreeMap<>();
    private final Set<Long> done = new HashSet<>();
    private long highest=-1;
    public long recovered, rejected;
    public VideoPackets(byte[] key, byte[] salt) {
        if(key.length!=32 || salt.length!=4) throw new IllegalArgumentException("Session key length");
        this.key=new SecretKeySpec(key,"AES"); this.salt=salt.clone();
    }
    public Frame feed(byte[] packet, int length, long now) {
        if(length<HEADER) return null;
        ByteBuffer b=ByteBuffer.wrap(packet,0,length);
        if(b.getInt()!=0x51445632) return null;
        long seq=Integer.toUnsignedLong(b.getInt());
        int w=b.getShort()&65535, h=b.getShort()&65535, flags=b.get()&255;
        long pts=b.getLong(); int index=b.getShort()&65535, count=b.getShort()&65535, total=b.getInt();
        if(w<1 || h<1 || w>8192 || h>8192 || (long)w*h>33554432 || flags>1 || pts<0
                || total<16 || total>MAX || count!=(total+CHUNK-1)/CHUNK
                || index>=count+(count+7)/8 || done.contains(seq) || seq<=highest-8) return null;
        int size=index>=count?CHUNK:Math.min(CHUNK,total-index*CHUNK);
        if(length!=HEADER+size) return null;
        pending.values().removeIf(a -> now-a.created>100000000L);
        Assembly a=pending.get(seq);
        if(a==null) {
            if(pending.size()>=8) pending.pollFirstEntry();
            a=new Assembly(seq,w,h,flags,pts,count,total,now); pending.put(seq,a);
        }
        if(a.width!=w || a.height!=h || a.flags!=flags || a.pts!=pts || a.count!=count || a.total!=total) return null;
        if(a.shards[index]==null) {
            a.shards[index]=Arrays.copyOfRange(packet,HEADER,length);
            if(index<count) a.present++;
        }
        // Only the affected FEC group needs inspection (linear total work).
        int start=index<count?index/8*8:(index-count)*8;
        byte[] parity=a.shards[count+start/8];
        if(parity!=null) {
            int missing=-1, n=0;
            for(int i=start;i<Math.min(start+8,count);i++) if(a.shards[i]==null) {missing=i; n++;}
            if(n==1) {
                byte[] result=parity.clone();
                for(int i=start;i<Math.min(start+8,count);i++) if(i!=missing) {
                    byte[] shard=a.shards[i];
                    for(int j=0;j<shard.length;j++) result[j]^=shard[j];
                }
                a.shards[missing]=Arrays.copyOf(result,Math.min(CHUNK,total-missing*CHUNK));
                a.present++; recovered++;
            }
        }
        if(a.present!=count) return null;
        pending.remove(seq);
        byte[] encrypted=new byte[total];
        for(int i=0;i<count;i++) System.arraycopy(a.shards[i],0,encrypted,i*CHUNK,a.shards[i].length);
        try {
            Cipher cipher=Cipher.getInstance("AES/GCM/NoPadding");
            byte[] nonce=ByteBuffer.allocate(12).put(salt).putLong(seq).array();
            cipher.init(Cipher.DECRYPT_MODE,key,new GCMParameterSpec(128,nonce));
            cipher.updateAAD(ByteBuffer.allocate(17).putInt((int)seq).putShort((short)w).putShort((short)h)
                    .put((byte)flags).putLong(pts).array());
            byte[] plain=cipher.doFinal(encrypted);
            highest=Math.max(highest,seq); done.add(seq); done.removeIf(s -> s<=highest-8);
            Frame result=new Frame(); result.sequence=seq; result.width=w; result.height=h;
            result.key=flags==1; result.pts=pts; result.data=plain; return result;
        } catch(Exception e) { rejected++; return null; }
    }
}
