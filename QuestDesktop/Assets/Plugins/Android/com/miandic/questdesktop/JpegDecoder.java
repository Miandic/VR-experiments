package com.miandic.questdesktop;

import android.graphics.Bitmap;
import android.graphics.BitmapFactory;
import java.nio.ByteBuffer;
import java.nio.ByteOrder;

/** Runs on the network worker, never on Unity's rendering/main thread. */
public final class JpegDecoder {
    private ByteBuffer pixels;
    private ByteBuffer input;
    private byte[] compressed;
    public float bitmapDecodeMs;
    public JpegDecoder() {}
    public ByteBuffer input(int length) {
        if (compressed == null || compressed.length < length) {
            int capacity = (length + 65535) / 65536 * 65536;
            compressed = new byte[capacity];
            input = ByteBuffer.allocateDirect(capacity);
        }
        input.clear();
        return input;
    }
    public ByteBuffer decode(int length, int width, int height) {
        input.position(0);
        input.get(compressed, 0, length);
        BitmapFactory.Options options = new BitmapFactory.Options();
        options.inPreferredConfig = Bitmap.Config.ARGB_8888;
        options.inScaled = false;
        long started = System.nanoTime();
        Bitmap bitmap = BitmapFactory.decodeByteArray(compressed, 0, length, options);
        bitmapDecodeMs = (System.nanoTime() - started) / 1000000f;
        if (bitmap == null) throw new IllegalArgumentException("Invalid JPEG");
        try {
            if (bitmap.getWidth() != width || bitmap.getHeight() != height)
                throw new IllegalArgumentException("JPEG dimensions differ from frame header");
            int bytes = width * height * 4;
            if (pixels == null || pixels.capacity() != bytes)
                pixels = ByteBuffer.allocateDirect(bytes).order(ByteOrder.nativeOrder());
            pixels.clear();
            // ARGB_8888's native packing is ABGR in a 32-bit word: little endian RGBA bytes.
            bitmap.copyPixelsToBuffer(pixels);
            pixels.rewind();
            return pixels;
        } finally { bitmap.recycle(); }
    }
}
