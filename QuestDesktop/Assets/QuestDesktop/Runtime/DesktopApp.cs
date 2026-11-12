using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.UI;

namespace QuestDesktop
{
    public sealed partial class DesktopApp : MonoBehaviour
    {
        sealed class ActionButton { public RectTransform rect; public Image image; public Action click; }
        readonly List<ActionButton> buttons = new List<ActionButton>();
        readonly Color normal = new Color(0.12f, 0.19f, 0.27f, 0.97f);
        ConnectionConfig connectionConfig;
        DisplayConfig display;
        DesktopConnection connection;
        VideoLayer video;
        long videoFrames, videoBytes;
        int sourceWidth = 16, sourceHeight = 9;
        int ImageWidth => video != null && video.Width > 0 ? video.Width : texture.width;
        int ImageHeight => video != null && video.Height > 0 ? video.Height : texture.height;
        Transform head;
        RectTransform screen, menu;
        RawImage picture;
        Texture2D texture, backTexture;
        Text status, info;
        Font font;
        bool remote, menuVisible = true, editingHost;
        string hostDraft = "", message = "Ready. Load connection.json, then Connect.";
        float nextStats, frames, bytes, fps, mbps, lastFrame;
        float statsStarted;
        bool menuUsesController;
        float decodeMs, bitmapDecodeMs;

        void Start()
        {
            try
            {
                connectionConfig = ConfigFiles.Load<ConnectionConfig>("connection.json");
                display = ConfigFiles.Load<DisplayConfig>("display.json");
            }
            catch (Exception error)
            {
                connectionConfig = new ConnectionConfig(); display = new DisplayConfig();
                message = "Config error: " + error.Message;
            }
            display.Clamp();
            statsStarted = Time.unscaledTime;
            var rig = FindFirstObjectByType<OVRCameraRig>();
            head = rig != null ? rig.centerEyeAnchor : Camera.main.transform;
            font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            screen = Canvas("Desktop screen", null, new Vector2(1600, 900));
            picture = screen.gameObject.AddComponent<RawImage>();
            picture.color = Color.black;
            texture = new Texture2D(2, 2, TextureFormat.RGB24, false, false);
            texture.filterMode = FilterMode.Bilinear;
            backTexture = new Texture2D(2, 2, TextureFormat.RGBA32, false, false);
            backTexture.filterMode = FilterMode.Bilinear;
            menu = Canvas("Desktop HUD", head, new Vector2(1040, 420));
            menu.GetComponent<Canvas>().sortingOrder = 100;
            menu.localPosition = new Vector3(0, -0.47f, 1.15f);
            menu.localScale = Vector3.one * 0.00075f;
            var bg = menu.gameObject.AddComponent<Image>(); bg.color = new Color(0.02f, 0.035f, 0.055f, 0.96f);
            BuildMenu();
            if (!display.hasWorldPose) Recenter();
            ApplyDisplay();
            menuUsesController = false;
            PlaceMenu();
            BuildHandControls(rig);
            SetMenu(!connectionConfig.autoConnect);
            if (connectionConfig.autoConnect) Connect();
        }

        RectTransform Canvas(string title, Transform parent, Vector2 size)
        {
            var go = new GameObject(title, typeof(RectTransform), typeof(Canvas));
            var rect = (RectTransform)go.transform; rect.SetParent(parent, false); rect.sizeDelta = size;
            go.GetComponent<Canvas>().renderMode = RenderMode.WorldSpace;
            return rect;
        }
        RectTransform Rect(string title, RectTransform parent, float x, float y, float w, float h)
        {
            var rect = (RectTransform)new GameObject(title, typeof(RectTransform)).transform;
            rect.SetParent(parent, false); rect.anchorMin = rect.anchorMax = new Vector2(0, 1);
            rect.pivot = new Vector2(0, 1); rect.anchoredPosition = new Vector2(x, -y);
            rect.sizeDelta = new Vector2(w, h); return rect;
        }
        Text Label(RectTransform parent, string text, float x, float y, float w, float h, int size = 23)
        {
            var label = Rect(text, parent, x, y, w, h).gameObject.AddComponent<Text>();
            label.font = font; label.text = text; label.fontSize = size; label.color = Color.white;
            label.alignment = TextAnchor.MiddleLeft; label.raycastTarget = false; return label;
        }
        void Button(string text, int col, int row, Action action)
        {
            var rect = Rect(text, menu, 20 + col * 255, 130 + row * 63, 235, 53);
            var image = rect.gameObject.AddComponent<Image>(); image.color = normal;
            var label = Label(rect, text, 10, 0, 215, 53); label.alignment = TextAnchor.MiddleCenter;
            buttons.Add(new ActionButton { rect = rect, image = image, click = action });
        }
        void BuildMenu()
        {
            foreach (Transform child in menu) Destroy(child.gameObject);
            buttons.Clear();
            status = Label(menu, "", 20, 10, 1000, 65, 22);
            info = Label(menu, "", 20, 78, 1000, 45, 20);
            if (editingHost)
            {
                string[] keys = { "1", "2", "3", "4", "5", "6", "7", "8", "9", ".", "0", "<" };
                for (int i = 0; i < keys.Length; i++)
                {
                    string key = keys[i];
                    Button(key, i % 3, i / 3, () => {
                        if (key == "<") { if (hostDraft.Length > 0) hostDraft = hostDraft.Substring(0, hostDraft.Length - 1); }
                        else if (hostDraft.Length < 64) hostDraft += key;
                    });
                }
                Button("Save IP", 3, 0, () => {
                    if (!System.Net.IPAddress.TryParse(hostDraft, out _)) { message = "Invalid IP address"; return; }
                    if (remote) connectionConfig.remoteHost = hostDraft; else connectionConfig.lanHost = hostDraft;
                    ConfigFiles.Save("connection.json", connectionConfig); editingHost = false; BuildMenu();
                });
                Button("Cancel", 3, 1, () => { editingHost = false; BuildMenu(); });
                return;
            }
            Button("Connect", 0, 0, Connect);
            Button("Disconnect", 1, 0, Disconnect);
            Button("LAN / Internet", 2, 0, () => { Disconnect(); remote = !remote; });
            Button("Edit IP", 3, 0, () => { hostDraft = remote ? connectionConfig.remoteHost : connectionConfig.lanHost; editingHost = true; BuildMenu(); });
            Button("World / HUD", 0, 1, () => { CancelHandCaptures(); display.hud = !display.hud; Change(); });
            Button("Recenter", 1, 1, () => { CancelHandCaptures(); Recenter(); Change(); });
            Button("Bring near", 2, 1, BringNear);
            Button("Reload config", 0, 2, () => {
                try { Disconnect(); connectionConfig = ConfigFiles.Load<ConnectionConfig>("connection.json"); message = "Connection config reloaded"; }
                catch (Exception error) { message = error.Message; }
            });
            Button("Hide menu", 1, 2, () => SetMenu(false));
            Label(menu, "Коснитесь кнопки пальцем или наведите луч и сделайте щипок.\nКрай экрана — перемещение; угол — размер; справа — ползунки.", 20, 330, 1000, 70, 21);
            // Always available gaze target when the main menu is hidden.
        }

        void Connect()
        {
            Disconnect();
            try
            {
                connectionConfig.NormalizeVideo();
                if (connectionConfig.legacyJpeg) connection = new DesktopConnection(connectionConfig, remote);
                else
                {
                    video = new GameObject("Video session").AddComponent<VideoLayer>();
                    video.Open(connectionConfig, remote, screen, display);
                    videoFrames = videoBytes = 0;
                }
                message = "Connecting";
            }
            catch (Exception error) { Disconnect(); message = error.Message; }
            lastFrame = Time.unscaledTime;
        }
        void Disconnect()
        {
            connection?.Dispose(); connection = null; message = "Disconnected";
            if (video != null) { video.Close(); video = null; }
            if (picture != null) { picture.texture = null; picture.color = Color.black; }
        }
        void Recenter()
        {
            display.worldPosition = head.position + head.forward * 2;
            display.worldEuler = Quaternion.LookRotation(head.forward, Vector3.up).eulerAngles;
            display.hasWorldPose = true;
            display.hudX = 0; display.hudY = 0.05f; display.hudDistance = 1.2f;
        }
        void Resize(float delta)
        {
            if (display.hud) display.hudWidth += delta; else display.width += delta;
            Change();
        }
        void Move(float x, float y, float z)
        {
            if (display.hud) { display.hudX += x; display.hudY += y; display.hudDistance += z; }
            else display.worldPosition += head.right * x + Vector3.up * y + head.forward * z;
            Change();
        }
        void Rotate(float x, float y)
        {
            if (display.hud) return;
            display.worldEuler += new Vector3(x, y, 0); Change();
        }
        void Change()
        {
            display.Clamp(); ApplyDisplay();
            try { ConfigFiles.Save("display.json", display); } catch (Exception error) { message = error.Message; }
        }
        void ApplyDisplay()
        {
            Transform intendedParent = display.hud ? head : null;
            if (screen.parent != intendedParent) screen.SetParent(intendedParent, false);
            if (display.hud)
            {
                screen.localPosition = new Vector3(display.hudX, display.hudY, display.hudDistance);
                screen.localRotation = Quaternion.identity;
            }
            else { screen.position = display.worldPosition; screen.rotation = Quaternion.Euler(display.worldEuler); }
            float width = display.hud ? display.hudWidth : display.width;
            float aspect = ImageWidth > 2 ? (float)ImageWidth / ImageHeight : 16f / 9f;
            screen.sizeDelta = new Vector2(1600, 1600 / aspect);
            screen.localScale = Vector3.one * (width / 1600);
            picture.color = picture.texture == null ? Color.black : new Color(display.brightness, display.brightness, display.brightness, display.hud ? display.hudOpacity : 1);
            if (video != null) picture.color = Color.clear;
            LayoutHandControls();
        }
        void SetMenu(bool visible)
        {
            CancelHandCaptures();
            menuVisible = visible; menu.gameObject.SetActive(visible);
            if (visible) PlaceMenu();
        }
        void PlaceMenu()
        {
            menu.SetParent(menuUsesController ? head : null, false);
            if (menuUsesController)
            {
                menu.localPosition = new Vector3(0, -0.47f, 1.15f);
                menu.localRotation = Quaternion.identity;
            }
            else
            {
                // A gaze target must stay still while the user turns their head toward it.
                menu.position = head.TransformPoint(new Vector3(0, -0.12f, 0.55f));
                menu.rotation = head.rotation;
            }
            menu.localScale = Vector3.one * 0.0006f;
        }

        void Update()
        {
            if (head == null) return;
            if (Input.GetKeyDown(KeyCode.Tab)) SetMenu(!menuVisible);
            if (video != null)
            {
                if (video.Rendered > videoFrames) lastFrame = Time.unscaledTime;
                frames += video.Rendered - videoFrames; bytes += video.Bytes - videoBytes;
                videoFrames = video.Rendered; videoBytes = video.Bytes;
                if (ImageWidth != sourceWidth || ImageHeight != sourceHeight)
                { sourceWidth = ImageWidth; sourceHeight = ImageHeight; ApplyDisplay(); }
                picture.color = Color.clear;
                if (!video.Alive || Time.unscaledTime - lastFrame > 15)
                { string reason = video.Alive ? "Video timeout" : video.Status; Disconnect(); message = reason; }
            }
            byte[] frame = connection?.TakeFrame();
            var pixels = connection?.TakePixels();
            if (frame != null || pixels != null)
            {
                try
                {
                    int previousWidth = texture.width, previousHeight = texture.height;
                    if (pixels != null)
                    {
                        if (backTexture.width != pixels.width || backTexture.height != pixels.height || backTexture.format != TextureFormat.RGBA32)
                            backTexture.Reinitialize(pixels.width, pixels.height, TextureFormat.RGBA32, false);
                        backTexture.LoadRawTextureData(pixels.rgba); backTexture.Apply(false, false);
                        decodeMs = pixels.decodeMs;
                        bitmapDecodeMs = pixels.bitmapDecodeMs;
                    }
                    else
                    {
                        float decodeStart = Time.realtimeSinceStartup;
                        if (!ImageConversion.LoadImage(backTexture, frame, false)) throw new Exception("Invalid JPEG image");
                        decodeMs = (Time.realtimeSinceStartup - decodeStart) * 1000;
                    }
                    // Never replace storage of the texture currently being drawn by the HUD.
                    var previous = texture; texture = backTexture; backTexture = previous;
                    picture.texture = texture;
                    picture.uvRect = pixels != null ? new Rect(0, 1, 1, -1) : new Rect(0, 0, 1, 1);
                    picture.color = new Color(display.brightness, display.brightness, display.brightness, display.hud ? display.hudOpacity : 1);
                    if (texture.width != previousWidth || texture.height != previousHeight) ApplyDisplay();
                    frames++; bytes += pixels != null ? pixels.jpegBytes : frame.Length; lastFrame = Time.unscaledTime;
                }
                catch (Exception error) { Disconnect(); message = error.Message; }
                finally { connection?.FrameDecoded(); }
            }
            if (connection != null && (!connection.IsAlive || Time.unscaledTime - lastFrame > 12))
            {
                string reason = connection.IsAlive ? "Stream timeout" : connection.Status;
                Disconnect(); message = reason;
            }
            if (Time.unscaledTime >= nextStats)
            {
                float elapsed = Mathf.Max(0.001f, Time.unscaledTime - statsStarted);
                fps = frames / elapsed; mbps = bytes * 8 / 1000000 / elapsed;
                frames = bytes = 0; statsStarted = Time.unscaledTime; nextStats = statsStarted + 1;
            }
            status.text = editingHost ? "PC IP: " + hostDraft + "\n" + message
                : (video?.Status ?? connection?.Status ?? message) + "\n" + (remote ? "Internet: " + connectionConfig.remoteHost : "LAN: " + connectionConfig.lanHost);
            info.text = $"{fps:F0} fps | {mbps:F1} Mbit/s | {ImageWidth}x{ImageHeight} | " +
                (display.hud ? $"HUD {display.hudWidth:F2}m / alpha {display.hudOpacity:F2}" : $"World {display.width:F2}m");
#if DEVELOPMENT_BUILD || UNITY_EDITOR
            DeviceDiagnostics();
#endif
        }

#if DEVELOPMENT_BUILD || UNITY_EDITOR
        [Serializable] sealed class DeviceCommand { public string action; }
        [Serializable] sealed class DeviceState
        {
            public string status, lastAction;
            public float streamFps, mbps, decodeMs, frameTimeMs, screenWidth, opacity;
            public int width, height;
            public bool hud, menuVisible, controller, screenFollowsHead;
            public Vector3 worldPosition, localPosition;
            public int handsTracked, handActions, directActions, rayActions, pokeActions;
            public string lastHandAction;
            public float brightness, sideVisibility;
            public bool manipulating;
            public float bitmapDecodeMs;
            public string sampledAtUtc;
            public Vector3 leftIndexOnScreen, rightIndexOnScreen;
            public string leftNearTarget, rightNearTarget;
            public bool leftConfident, rightConfident;
            public string videoDecoder;
            public long videoReceived, videoDecoded, videoRendered, videoDropped, videoRecovered, videoCorrupt;
        }
        float nextDiagnostic;
        string lastDiagnosticAction = "";
        void DeviceDiagnostics()
        {
            if (Time.unscaledTime < nextDiagnostic) return;
            nextDiagnostic = Time.unscaledTime + 1;
            try
            {
                string command = Path.Combine(Application.persistentDataPath, "device-command.json");
                if (File.Exists(command))
                {
                    var request = JsonUtility.FromJson<DeviceCommand>(File.ReadAllText(command));
                    File.Delete(command);
                    var button = buttons.Find(item => item.rect.name == request.action);
                    if (button != null) { button.click(); lastDiagnosticAction = request.action; }
                    else if (request.action == "Show menu") { SetMenu(true); lastDiagnosticAction = request.action; }
                }
                var state = new DeviceState {
                    status = video?.Status ?? connection?.Status ?? message, lastAction = lastDiagnosticAction,
                    streamFps = fps, mbps = mbps, decodeMs = decodeMs, frameTimeMs = Time.unscaledDeltaTime * 1000,
                    width = ImageWidth, height = ImageHeight, hud = display.hud, menuVisible = menuVisible,
                    videoDecoder = video?.Decoder ?? "JPEG", videoReceived = video?.Received ?? 0,
                    videoDecoded = video?.Decoded ?? 0, videoRendered = video?.Rendered ?? 0,
                    videoDropped = video?.Dropped ?? 0, videoRecovered = video?.Recovered ?? 0, videoCorrupt = video?.Corrupt ?? 0,
                    controller = menuUsesController, screenFollowsHead = screen.parent == head,
                    screenWidth = display.hud ? display.hudWidth : display.width, opacity = display.hud ? display.hudOpacity : 1,
                    worldPosition = screen.position, localPosition = screen.localPosition,
                    handsTracked = (handSessions[0].frame.tracked ? 1 : 0) + (handSessions[1].frame.tracked ? 1 : 0),
                    handActions = handActionCount, directActions = directActionCount, rayActions = rayActionCount,
                    pokeActions = pokeActionCount, lastHandAction = lastHandAction, brightness = display.brightness,
                    sideVisibility = sideGroup != null ? sideGroup.alpha : 0, manipulating = manipulationOwner >= 0,
                    bitmapDecodeMs = bitmapDecodeMs, sampledAtUtc = DateTime.UtcNow.ToString("o"),
                    leftIndexOnScreen = screen.InverseTransformPoint(handSessions[0].frame.indexTip) * ScreenScale,
                    rightIndexOnScreen = screen.InverseTransformPoint(handSessions[1].frame.indexTip) * ScreenScale,
                    leftNearTarget = PickDirect(handSessions[0].frame, out _)?.rect.name ?? "",
                    rightNearTarget = PickDirect(handSessions[1].frame, out _)?.rect.name ?? "",
                    leftConfident = handSessions[0].frame.confident, rightConfident = handSessions[1].frame.confident
                };
                File.WriteAllText(Path.Combine(Application.persistentDataPath, "device-state.json"), JsonUtility.ToJson(state, true));
            }
            catch (Exception error) { Debug.LogWarning("[QuestDesktop] Diagnostics: " + error.Message); }
        }
#endif
        static bool Hit(RectTransform rect, Vector3 origin, Vector3 direction, out Vector3 point)
        {
            var plane = new Plane(rect.forward, rect.position);
            var ray = new Ray(origin, direction);
            point = Vector3.zero;
            if (!plane.Raycast(ray, out float distance) || distance > 5) return false;
            point = ray.GetPoint(distance);
            Vector3 local = rect.InverseTransformPoint(point);
            return rect.rect.Contains(new Vector2(local.x, local.y));
        }
        void OnApplicationPause(bool paused)
        {
            if (paused) { CancelHandCaptures(); Disconnect(); }
            else if (head != null && connectionConfig != null && connectionConfig.autoConnect && connection == null && video == null) Connect();
        }
        void OnDestroy()
        {
            Disconnect();
            DisposeHandControls();
            if (texture != null) Destroy(texture);
            if (backTexture != null) Destroy(backTexture);
            if (screen != null) Destroy(screen.gameObject);
            if (menu != null) Destroy(menu.gameObject);
        }
    }
}

