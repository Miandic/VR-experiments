using System;
using UnityEngine;

namespace QuestDesktop
{
    // The compositor samples MediaCodec's Surface directly. No Unity video texture or CPU upload.
    // Lives separately from DesktopApp so disconnect can release MediaCodec before the Surface.
    public sealed class VideoLayer : MonoBehaviour
    {
        public int Width { get; private set; }
        public int Height { get; private set; }
        public long Received, Decoded, Rendered, Dropped, Bytes, Recovered, Corrupt;
        public string Status = "Connecting", Decoder = "";
        public bool Alive = true;
        public bool Visible => Decoded > 0 && overlay != null;
        RectTransform screen;
        DisplayConfig display;
        OVROverlay overlay;
        int layerWidth, layerHeight;
        bool closing, detached;
        float nextStats;
#if UNITY_ANDROID && !UNITY_EDITOR
        AndroidJavaObject client;
#endif
        public void Open(ConnectionConfig config, bool remote, RectTransform target, DisplayConfig settings)
        {
            screen = target; display = settings;
#if UNITY_ANDROID && !UNITY_EDITOR
            client = new AndroidJavaObject("com.miandic.questdesktop.VideoClient",
                remote ? config.remoteHost : config.lanHost, config.port, config.token,
                config.certificateSha256, config.videoCodec, config.videoFps, config.videoBitrateMbps * 1000000);
#else
            throw new PlatformNotSupportedException("QDS2 Surface playback requires Quest; use tools/video_simulator.py for local testing or legacyJpeg=true for Editor preview");
#endif
        }
        void Update()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            if (client == null) return;
            if (closing)
            {
                // Stop decoding before releasing the compositor's Java Surface reference.
                if (client.Get<bool>("released") && !client.Get<bool>("alive"))
                {
                    client.Dispose(); client = null;
                    if (overlay != null) Destroy(overlay.gameObject);
                    Destroy(gameObject);
                }
                return;
            }
            if (Time.unscaledTime >= nextStats)
            {
                nextStats = Time.unscaledTime + 0.1f;
                Width = client.Get<int>("width"); Height = client.Get<int>("height");
                Status = client.Get<string>("status"); Decoder = client.Get<string>("decoderName");
                Alive = client.Get<bool>("alive");
                Received = client.Get<long>("received"); Decoded = client.Get<long>("decoded");
                Rendered = client.Get<long>("rendered"); Dropped = client.Get<long>("dropped");
                Bytes = client.Get<long>("bytes"); Recovered = client.Get<long>("recovered"); Corrupt = client.Get<long>("corrupt");
            }
            if (Width < 1 || Height < 1 || !Alive) return;
            if (layerWidth != Width || layerHeight != Height)
            {
                if (!detached) { client.Call("setSurface", (AndroidJavaObject)null, 0, 0); detached = true; }
                if (client.Get<bool>("surfaceBusy") || client.Get<int>("surfaceAck") != client.Get<int>("surfaceEpoch")) return;
                if (overlay != null) { Destroy(overlay.gameObject); overlay = null; return; }
                var go = new GameObject("Hardware video compositor");
                go.SetActive(false);
                overlay = go.AddComponent<OVROverlay>();
                overlay.isExternalSurface = true; overlay.isDynamic = true;
                overlay.externalSurfaceWidth = Width; overlay.externalSurfaceHeight = Height;
                overlay.currentOverlayType = OVROverlay.OverlayType.Underlay;
                overlay.currentOverlayShape = OVROverlay.OverlayShape.Quad;
                overlay.compositionDepth = 0; // Passthrough uses int.MaxValue; Unity controls stay above both.
                overlay.overridePerLayerColorScaleAndOffset = true;
                overlay.isAlphaPremultiplied = true;
                overlay.externalSurfaceObjectCreated = () => {
                    if (closing || client == null || overlay == null) return;
                    using (var surface = new AndroidJavaObject(overlay.externalSurfaceObject))
                        client.Call("setSurface", surface, layerWidth, layerHeight);
                };
                layerWidth = Width; layerHeight = Height; detached = false;
                SyncTransform(); go.SetActive(true);
            }
            SyncTransform();
#endif
        }
        void SyncTransform()
        {
            if (overlay == null || screen == null) return;
            // Parent preserves the runtime's head-locked composition flag in HUD mode.
            var parent = display.hud ? screen.parent : null;
            if (overlay.transform.parent != parent) overlay.transform.SetParent(parent, true);
            overlay.transform.SetPositionAndRotation(screen.position, screen.rotation);
            overlay.transform.localScale = new Vector3(screen.rect.width * screen.lossyScale.x,
                screen.rect.height * screen.lossyScale.y, 1);
            float alpha = display.hud ? display.hudOpacity : 1;
            float color = display.brightness * alpha;
            overlay.colorScale = new Vector4(color, color, color, alpha);
            overlay.hidden = closing || Decoded == 0;
        }
        void LateUpdate() { if (!closing) SyncTransform(); }
        public void Close()
        {
            closing = true; Alive = false;
            if (overlay != null) overlay.hidden = true;
#if UNITY_ANDROID && !UNITY_EDITOR
            if (client != null) client.Call("close"); else Destroy(gameObject);
#else
            Destroy(gameObject);
#endif
        }
    }
}
