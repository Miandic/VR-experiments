import com.miandic.questdesktop.VideoPackets;
import java.io.*;
import java.security.MessageDigest;

/** Runs the exact APK's packet receiver on desktop JVM, with Python-generated fixtures. */
public final class VideoPacketsHarness {
    public static void main(String[] args) throws Exception {
        try(DataInputStream in=new DataInputStream(new FileInputStream(args[0]))) {
            byte[] key=new byte[32],salt=new byte[4];in.readFully(key);in.readFully(salt);
            VideoPackets receiver=new VideoPackets(key,salt);
            int count=in.readInt();long time=0;
            for(int i=0;i<count;i++) {
                byte[] packet=new byte[in.readInt()];in.readFully(packet);
                VideoPackets.Frame frame=receiver.feed(packet,packet.length,time++);
                if(frame!=null) {
                    byte[] hash=MessageDigest.getInstance("SHA-256").digest(frame.data);
                    StringBuilder text=new StringBuilder();for(byte b:hash) text.append(String.format("%02x",b));
                    System.out.println(frame.sequence+" "+frame.width+" "+frame.height+" "+text);
                }
            }
        }
    }
}
