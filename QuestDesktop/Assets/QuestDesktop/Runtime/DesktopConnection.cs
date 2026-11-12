using System;
using System.IO;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using UnityEngine;

namespace QuestDesktop
{
    // Each session owns its worker/socket and one pending frame. Android decodes on that worker.
    public sealed class DesktopConnection : IDisposable
    {
        [Serializable] sealed class Auth { public string protocol = "QDS1"; public string token; }
        [Serializable] sealed class Reply { public bool ok; public string error; }
        readonly ManualResetEvent decoded = new ManualResetEvent(false);
        readonly object lifecycle = new object();
        readonly TcpClient socket = new TcpClient();
        readonly Thread worker;
        readonly string host, pin, auth;
        readonly int port;
        volatile bool stopped;
        public volatile string Status = "Connecting";
        public volatile byte[] Pending;
        public sealed class PixelFrame
        {
            public byte[] rgba;
            public int width, height, jpegBytes;
            public float decodeMs, bitmapDecodeMs;
        }
        PixelFrame pendingPixels;
#if UNITY_ANDROID && !UNITY_EDITOR
        readonly AndroidJavaObject jpegDecoder;
        byte[] rgbaBuffer;
#endif
        public PixelFrame TakePixels() => Interlocked.Exchange(ref pendingPixels, null);
        public bool IsAlive => worker.IsAlive;

        public DesktopConnection(ConnectionConfig config, bool remote)
        {
            host = remote ? config.remoteHost : config.lanHost;
            port = config.port;
            pin = config.certificateSha256.Replace(":", "").Trim().ToLowerInvariant();
            auth = JsonUtility.ToJson(new Auth { token = config.token });
            if (string.IsNullOrWhiteSpace(host) || pin.Length != 64 || string.IsNullOrEmpty(config.token)
                || port < 1 || port > 65535)
                throw new ArgumentException("Configure host, token and SHA256 pin in connection.json");
#if UNITY_ANDROID && !UNITY_EDITOR
            // Resolve the Java class on Unity's thread before attaching the network worker to JNI.
            jpegDecoder = new AndroidJavaObject("com.miandic.questdesktop.JpegDecoder");
#endif
            worker = new Thread(Run) { IsBackground = true, Name = "QuestDesktop TLS" };
            worker.Start();
        }

        bool ValidateCertificate(object sender, X509Certificate certificate, X509Chain chain, SslPolicyErrors errors)
        {
            if (certificate == null) return false;
            using (var sha = SHA256.Create())
            {
                string actual = BitConverter.ToString(sha.ComputeHash(certificate.GetRawCertData()))
                    .Replace("-", "").ToLowerInvariant();
                int difference = 0;
                for (int i = 0; i < actual.Length; i++) difference |= actual[i] ^ pin[i];
                return difference == 0;
            }
        }

        void Run()
        {
            try
            {
#if UNITY_ANDROID && !UNITY_EDITOR
                AndroidJNI.AttachCurrentThread();
#endif
                var connect = socket.BeginConnect(host, port, null, null);
                using (connect.AsyncWaitHandle)
                    if (!connect.AsyncWaitHandle.WaitOne(8000)) throw new IOException("Connection timeout");
                socket.EndConnect(connect);
                socket.NoDelay = true;
                socket.ReceiveTimeout = socket.SendTimeout = 10000;
                using (var tls = new SslStream(socket.GetStream(), false, ValidateCertificate))
                {
                    tls.ReadTimeout = tls.WriteTimeout = 10000;
                    tls.AuthenticateAsClient(host, null, SslProtocols.Tls12, false);
                    byte[] request = Encoding.UTF8.GetBytes(auth);
                    WriteInt(tls, request.Length);
                    tls.Write(request, 0, request.Length);
                    int length = ReadInt(tls);
                    if (length < 1 || length > 4096) throw new IOException("Invalid handshake");
                    var reply = JsonUtility.FromJson<Reply>(Encoding.UTF8.GetString(Read(tls, length)));
                    if (!reply.ok) throw new IOException(reply.error ?? "Authentication failed");
                    Status = "Connected";
                    while (!stopped)
                    {
                        decoded.Reset();
                        tls.WriteByte((byte)'N');
                        int size = ReadInt(tls), width = ReadInt(tls), height = ReadInt(tls);
                        if (size < 1 || size > 32 * 1024 * 1024 || width < 1 || height < 1
                            || width > 8192 || height > 8192) throw new IOException("Invalid frame bounds");
                        var frame = Read(tls, size);
                        if (stopped) break;
#if UNITY_ANDROID && !UNITY_EDITOR
                        var timer = System.Diagnostics.Stopwatch.StartNew();
                        if (rgbaBuffer == null || rgbaBuffer.Length != width * height * 4) rgbaBuffer = new byte[width * height * 4];
                        var rgba = rgbaBuffer;
                        using (var input = jpegDecoder.Call<AndroidJavaObject>("input", frame.Length))
                            AndroidJNI.GetDirectByteBuffer(input.GetRawObject()).GetSubArray(0, frame.Length).CopyFrom(frame);
                        using (var buffer = jpegDecoder.Call<AndroidJavaObject>("decode", frame.Length, width, height))
                        {
                            var native = AndroidJNI.GetDirectByteBuffer(buffer.GetRawObject());
                            if (native.Length != rgba.Length) throw new IOException("Invalid decoded pixel buffer");
                            native.CopyTo(rgba); // Bulk copy, no JNI/C# call per pixel. Java owns this memory.
                        }
                        if (stopped) break;
                        Interlocked.Exchange(ref pendingPixels, new PixelFrame { rgba = rgba, width = width,
                            height = height, jpegBytes = size, decodeMs = (float)timer.Elapsed.TotalMilliseconds,
                            bitmapDecodeMs = jpegDecoder.Get<float>("bitmapDecodeMs") });
#else
                        Pending = frame;
#endif
                        // Pull next image only when the current image has been decoded.
                        if (!decoded.WaitOne(10000)) throw new IOException("Display timeout");
                    }
                }
            }
            catch (Exception error)
            {
                if (!stopped) Status = "Disconnected: " + error.Message;
            }
            finally
            {
                socket.Close();
#if UNITY_ANDROID && !UNITY_EDITOR
                jpegDecoder.Dispose();
                AndroidJNI.DetachCurrentThread();
#endif
                lock (lifecycle) { stopped = true; decoded.Dispose(); }
            }
        }

        public byte[] TakeFrame() => Interlocked.Exchange(ref Pending, null);
        public void FrameDecoded() { lock (lifecycle) { if (!stopped) decoded.Set(); } }
        static byte[] Read(Stream stream, int size)
        {
            var bytes = new byte[size];
            int offset = 0;
            while (offset < size)
            {
                int count = stream.Read(bytes, offset, size - offset);
                if (count == 0) throw new EndOfStreamException("Host disconnected");
                offset += count;
            }
            return bytes;
        }
        static int ReadInt(Stream stream)
        {
            byte[] b = Read(stream, 4);
            return (b[0] << 24) | (b[1] << 16) | (b[2] << 8) | b[3];
        }
        static void WriteInt(Stream stream, int value)
        {
            byte[] b = { (byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value };
            stream.Write(b, 0, 4);
        }
        public void Dispose()
        {
            lock (lifecycle)
            {
                if (!stopped) { stopped = true; decoded.Set(); }
                Pending = null;
                Interlocked.Exchange(ref pendingPixels, null);
            }
            socket.Close();
            // Worker disposes its event on exit; never block rendering on DNS/TLS.
        }
    }
}
