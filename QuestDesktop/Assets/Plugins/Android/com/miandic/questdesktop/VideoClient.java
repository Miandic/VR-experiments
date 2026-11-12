package com.miandic.questdesktop;

import android.media.*;
import android.os.*;
import android.view.Surface;
import org.json.JSONObject;
import java.io.*;
import java.net.*;
import java.nio.ByteBuffer;
import java.security.*;
import java.security.cert.X509Certificate;
import java.util.*;
import java.util.concurrent.ArrayBlockingQueue;
import javax.net.ssl.*;
import javax.crypto.Mac;
import javax.crypto.spec.SecretKeySpec;

/** TLS control + UDP/FEC worker + independent hardware decoder → compositor Surface.
 * Only compressed access units touch Java memory. No decoded pixel buffers/JNI copies.
 */
public final class VideoClient {
    public volatile String status="Connecting", decoderName="";
    public volatile int width, height;
    public volatile long received, decoded, rendered, dropped, bytes, recovered, corrupt;
    public volatile boolean alive=true;
    public volatile boolean surfaceBusy, released;
    public volatile int surfaceEpoch, surfaceAck;
    private volatile boolean stopped;
    private volatile Surface surface;
    private volatile int surfaceWidth, surfaceHeight;
    private SSLSocket tls;
    private DatagramSocket udp;
    private final ArrayBlockingQueue<VideoPackets.Frame> queue=new ArrayBlockingQueue<>(3);
    private final String host, token, pin, codec;
    private final int port, fps, bitrate;
    private volatile int negotiatedFps;
    private final Thread network, decode;
    public VideoClient(String host,int port,String token,String pin,String codec,int fps,int bitrate) {
        this.host=host;this.port=port;this.token=token;this.pin=pin.replace(":","").toLowerCase(Locale.ROOT);
        this.codec=codec;this.fps=fps;this.bitrate=bitrate;
        negotiatedFps=fps;
        if(this.pin.length()!=64 || token.isEmpty() || host.isEmpty()) throw new IllegalArgumentException("Configure host, token and pin");
        network=new Thread(this::networkLoop,"QDS2 UDP"); decode=new Thread(this::decodeLoop,"QDS2 MediaCodec");
        network.start(); decode.start();
    }
    public void setSurface(Surface value,int w,int h) {surfaceWidth=w;surfaceHeight=h;surface=value;surfaceEpoch++;}
    public void close() {
        stopped=true;
        try {if(tls!=null) tls.close();} catch(Exception ignored) {}
        if(udp!=null) udp.close();
        network.interrupt(); decode.interrupt();
    }
    // Called off Unity's main thread before the old compositor Surface is destroyed.
    public void awaitClosed() {
        close();
        try {network.join(2000); decode.join(2000);} catch(InterruptedException e) {Thread.currentThread().interrupt();}
    }
    private static JSONObject readJson(DataInputStream in) throws Exception {
        int size=in.readInt();if(size<1 || size>4096) throw new IOException("JSON bounds");
        byte[] b=new byte[size];in.readFully(b);return new JSONObject(new String(b,"UTF-8"));
    }
    private static void writeJson(DataOutputStream out,JSONObject json) throws Exception {
        byte[] b=json.toString().getBytes("UTF-8");out.writeInt(b.length);out.write(b);out.flush();
    }
    private void networkLoop() {
        try {
            TrustManager[] trust={new X509TrustManager() {
                public X509Certificate[] getAcceptedIssuers(){return new X509Certificate[0];}
                public void checkClientTrusted(X509Certificate[] c,String a) throws java.security.cert.CertificateException {throw new java.security.cert.CertificateException();}
                public void checkServerTrusted(X509Certificate[] c,String a) throws java.security.cert.CertificateException {
                    try {
                        byte[] actual=MessageDigest.getInstance("SHA-256").digest(c[0].getEncoded());
                        byte[] expected=new byte[32];for(int i=0;i<32;i++) expected[i]=(byte)Integer.parseInt(pin.substring(i*2,i*2+2),16);
                        if(!MessageDigest.isEqual(actual,expected)) throw new Exception("Certificate pin mismatch");
                    } catch(Exception e) {throw new java.security.cert.CertificateException(e);}
                }
            }};
            SSLContext context=SSLContext.getInstance("TLS");context.init(null,trust,new SecureRandom());
            tls=(SSLSocket)context.getSocketFactory().createSocket();
            tls.connect(new InetSocketAddress(host,port),8000);tls.setSoTimeout(8000);tls.setTcpNoDelay(true);
            tls.setEnabledProtocols(new String[]{"TLSv1.2"});tls.startHandshake();
            DataInputStream in=new DataInputStream(tls.getInputStream());
            DataOutputStream out=new DataOutputStream(tls.getOutputStream());
            writeJson(out,new JSONObject().put("protocol","QDS2").put("token",token).put("codec",codec).put("fps",fps).put("bitrate",bitrate));
            JSONObject reply=readJson(in);
            if(!reply.getBoolean("ok")) throw new IOException(reply.optString("error"));
            if(!codec.equals(reply.getString("codec"))) throw new IOException("Codec negotiation mismatch");
            negotiatedFps=reply.getInt("fps");
            byte[] key=android.util.Base64.decode(reply.getString("key"),0), salt=android.util.Base64.decode(reply.getString("salt"),0);
            VideoPackets packets=new VideoPackets(key,salt);
            udp=new DatagramSocket();udp.setReceiveBufferSize(4*1024*1024);udp.setSoTimeout(4);
            udp.connect(tls.getInetAddress(),reply.getInt("udpPort"));
            Mac mac=Mac.getInstance("HmacSHA256");mac.init(new SecretKeySpec(key,"HmacSHA256"));
            byte[] hello=mac.doFinal("QDS2 hello".getBytes("UTF-8"));
            udp.send(new DatagramPacket(hello,hello.length));
            byte[] buffer=new byte[1280];
            TreeMap<Long,VideoPackets.Frame> ready=new TreeMap<>();
            long expected=-1, gapSince=0, lastPacket=System.nanoTime(), nextReport=0, nextHello=0;
            boolean waitingKey=true;
            while(!stopped) {
                long now=System.nanoTime();
                try {
                    DatagramPacket datagram=new DatagramPacket(buffer,buffer.length);udp.receive(datagram);
                    VideoPackets.Frame f=packets.feed(buffer,datagram.getLength(),now);
                    if(f!=null) {
                        lastPacket=now;received++;bytes+=f.data.length;
                        if(f.sequence>=expected) ready.put(f.sequence,f);
                    }
                } catch(SocketTimeoutException ignored) {}
                if(received==0 && now>=nextHello) {udp.send(new DatagramPacket(hello,hello.length));nextHello=now+500000000L;}
                if(now-lastPacket>10000000000L) throw new IOException("Video timeout");
                while(!ready.isEmpty()) {
                    VideoPackets.Frame f=ready.firstEntry().getValue();
                    if(expected>=0 && f.sequence!=expected && !f.key) {
                        if(gapSince==0) gapSince=now;
                        if(now-gapSince<12000000L && ready.size()<4) break;
                        waitingKey=true;
                    }
                    ready.pollFirstEntry();gapSince=0;expected=f.sequence+1;
                    if(waitingKey && !f.key) {dropped++;continue;}
                    if(f.key) waitingKey=false;
                    width=f.width;height=f.height;
                    if(!queue.offer(f)) {
                        dropped+=queue.size()+1;queue.clear();waitingKey=true;
                        if(f.key) {queue.offer(f);waitingKey=false;}
                    }
                }
                recovered=packets.recovered;corrupt=packets.rejected;
                if(now>=nextReport) {
                    writeJson(out,new JSONObject().put("received",received).put("decoded",decoded)
                            .put("rendered",rendered).put("dropped",dropped).put("recovered",recovered));
                    nextReport=now+1000000000L;
                }
            }
        } catch(Exception e) {if(!stopped) status="Disconnected: "+e.getMessage();}
        finally {close();alive=false;}
    }
    private MediaCodec createDecoder(int w,int h,Surface target) throws Exception {
        String mime=codec.equals("hevc")?"video/hevc":"video/avc";
        MediaCodecInfo selected=null;
        for(MediaCodecInfo info:new MediaCodecList(MediaCodecList.ALL_CODECS).getCodecInfos()) {
            if(info.isEncoder() || !info.isHardwareAccelerated()) continue;
            if(!Arrays.asList(info.getSupportedTypes()).contains(mime)) continue;
            MediaCodecInfo.CodecCapabilities caps=info.getCapabilitiesForType(mime);
            if(!caps.getVideoCapabilities().areSizeAndRateSupported(w,h,negotiatedFps)) continue;
            if(selected==null || info.getName().contains("low_latency")) selected=info;
            if(info.getName().contains("low_latency")) break;
        }
        if(selected==null) throw new IOException("No hardware decoder for "+w+"x"+h+"@"+negotiatedFps);
        MediaFormat format=MediaFormat.createVideoFormat(mime,w,h);
        format.setInteger(MediaFormat.KEY_MAX_INPUT_SIZE,VideoPackets.MAX);
        format.setInteger(MediaFormat.KEY_LOW_LATENCY,1);
        format.setInteger(MediaFormat.KEY_PRIORITY,0);
        format.setInteger(MediaFormat.KEY_OPERATING_RATE,negotiatedFps);
        MediaCodec result=MediaCodec.createByCodecName(selected.getName());
        try {
            result.configure(format,target,null,0);result.start();decoderName=selected.getName();
            result.setOnFrameRenderedListener((c,pts,ns)->rendered++,new Handler(Looper.getMainLooper()));
            return result;
        } catch(Exception e) {result.release();throw e;}
    }
    private void decodeLoop() {
        MediaCodec decoder=null;Surface attached=null;int w=0,h=0;
        long expected=-1;
        VideoPackets.Frame pending=null;
        MediaCodec.BufferInfo info=new MediaCodec.BufferInfo();
        try {
            while(!stopped) {
                int epoch=surfaceEpoch;
                Surface target=surface;
                if(decoder!=null && (target!=attached || width!=w || height!=h)) {
                    decoder.stop();decoder.release();decoder=null;surfaceBusy=false;pending=null;expected=-1;
                }
                // Ack only on the decoder thread, after old decoder release. Unity must wait
                // for this epoch before destroying an old Surface (including create-in-flight).
                surfaceAck=epoch;
                if(target==null || !target.isValid() || surfaceWidth!=width || surfaceHeight!=height) {Thread.sleep(2);continue;}
                if(pending==null) pending=queue.poll();
                if(pending!=null) {
                    if(pending.width!=width || pending.height!=height) {pending=null;dropped++;continue;}
                    if(expected!=pending.sequence && !pending.key) {pending=null;dropped++;continue;}
                    if(decoder==null) {
                        if(!pending.key) {pending=null;continue;}
                        w=pending.width;h=pending.height;attached=target;
                        decoder=createDecoder(w,h,target);surfaceBusy=true;
                    } else if(expected!=pending.sequence && pending.key) decoder.flush();
                    int index=decoder.dequeueInputBuffer(0);
                    if(index>=0) {
                        ByteBuffer input=decoder.getInputBuffer(index);
                        if(input.capacity()<pending.data.length) throw new IOException("Decoder input too small");
                        input.clear();input.put(pending.data);
                        decoder.queueInputBuffer(index,0,pending.data.length,pending.pts,0);
                        expected=pending.sequence+1;pending=null;
                    }
                }
                if(decoder!=null) {
                    int index;
                    while((index=decoder.dequeueOutputBuffer(info,0))>=0) {
                        decoded++;decoder.releaseOutputBuffer(index,true);
                        status="Connected "+codec.toUpperCase(Locale.ROOT)+" hardware";
                    }
                }
                Thread.sleep(1);
            }
        } catch(Exception e) {if(!stopped) status="Decoder: "+e.getMessage();close();alive=false;}
        finally {if(decoder!=null) {try {decoder.stop();} catch(Exception ignored) {}decoder.release();}surfaceBusy=false;released=true;}
    }
}
