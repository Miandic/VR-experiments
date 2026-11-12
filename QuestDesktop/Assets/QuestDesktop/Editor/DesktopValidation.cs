using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UI;
using Debug = UnityEngine.Debug;

namespace QuestDesktop.EditorTools
{
    public static class DesktopValidation
    {
        static readonly BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        static void Require(bool condition, string reason) { if (!condition) throw new Exception(reason); }
        static void Invoke(object target, string method) => target.GetType().GetMethod(method, Private).Invoke(target, null);
        static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, Private).GetValue(target);

        public static void Run()
        {
            CheckConnection("valid"); CheckConnection("pin"); CheckConnection("token");
            EditorSceneManager.OpenScene("Assets/Scenes/Desktop.unity");
            var app = UnityEngine.Object.FindFirstObjectByType<DesktopApp>();
            Require(app != null, "DesktopApp missing from scene");
            Invoke(app, "Start");
            var display = Field<DisplayConfig>(app, "display");
            var screen = Field<RectTransform>(app, "screen");
            var head = Field<Transform>(app, "head");
            var menu = Field<RectTransform>(app, "menu");
            app.GetType().GetField("menuUsesController", Private).SetValue(app, false);
            Invoke(app, "PlaceMenu");
            Require(menu.parent == null, "Gaze menu follows head and cannot be targeted");
            var menuPosition = menu.position;
            var menuRotation = menu.rotation;
            var previousHeadRotation = head.rotation;
            head.rotation = Quaternion.Euler(0, 25, 0) * head.rotation;
            Require(menu.position == menuPosition && menu.rotation == menuRotation, "Gaze menu moved while aiming");
            head.rotation = previousHeadRotation;
            var image = Field<RawImage>(app, "picture");
            display.hud = false;
            Invoke(app, "ApplyDisplay");
            Require(screen.parent == null, "World display follows head");
            Vector3 worldPosition = screen.position;
            head.position += Vector3.right;
            Require(screen.position == worldPosition, "World screen moved with head");
            display.hud = true; display.hudX = 0.2f; display.hudY = -0.1f;
            image.texture = Field<Texture2D>(app, "texture");
            display.hudOpacity = 0.35f;
            Invoke(app, "ApplyDisplay");
            Require(screen.parent == head, "HUD does not follow head");
            Require(Mathf.Abs(screen.localPosition.x - 0.2f) < 0.001f, "HUD position mismatch");
            Require(Mathf.Abs(image.color.a - 0.35f) < 0.001f, "HUD alpha mismatch");
            display.hud = false; Invoke(app, "ApplyDisplay");
            Require(image.color.a == 1, "HUD alpha leaked into world mode");
            Require(app.GetType().GetField("buttons", Private).GetValue(app) is System.Collections.ICollection controls
                && controls.Count >= 8, "Menu controls missing");
            app.VerifyHandInteractions();
            UnityEngine.Object.DestroyImmediate(app.gameObject);
            Debug.Log("[DesktopValidation] PASS: C# TLS/JPEG, certificate and token rejection, world/HUD transforms, opacity and controls");
        }

        static void CheckConnection(string mode)
        {
            string root = Path.GetFullPath(Path.Combine(Application.dataPath, "../.."));
            string temp = Path.Combine(Path.GetTempPath(), "questdesktop-check-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(temp);
            var start = new ProcessStartInfo(Path.Combine(root, ".venv/Scripts/python.exe"))
            {
                Arguments = "\"" + Path.Combine(root, "tests/fixture_host.py") + "\" \"" + temp + "\"",
                UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = root
            };
            using (var process = Process.Start(start))
            {
                DesktopConnection connection = null;
                Texture2D texture = null;
                try
                {
                    string path = Path.Combine(temp, "connection.json");
                    var timer = Stopwatch.StartNew();
                    while (!File.Exists(path) && timer.ElapsedMilliseconds < 10000 && !process.HasExited) Thread.Sleep(20);
                    Require(File.Exists(path), "Python TLS fixture did not start");
                    var config = JsonUtility.FromJson<ConnectionConfig>(File.ReadAllText(path));
                    if (mode == "pin") config.certificateSha256 = new string('0', 64);
                    if (mode == "token") config.token = "invalid-token";
                    connection = new DesktopConnection(config, false);
                    timer.Restart();
                    while (connection.Pending == null && connection.IsAlive && timer.ElapsedMilliseconds < 10000) Thread.Sleep(10);
                    if (mode == "valid")
                    {
                        var frame = connection.TakeFrame();
                        Require(frame != null, "C# client did not receive JPEG: " + connection.Status);
                        texture = new Texture2D(2, 2, TextureFormat.RGB24, false);
                        Require(ImageConversion.LoadImage(texture, frame), "Unity could not decode JPEG");
                        Require(texture.width == 64 && texture.height == 32, "Wrong frame dimensions");
                        connection.FrameDecoded();
                        Debug.Log("[DesktopValidation] C# TLS + pinned certificate + Unity JPEG decode: PASS");
                    }
                    else
                    {
                        Require(!connection.IsAlive && connection.Pending == null, "Invalid " + mode + " was not rejected");
                        Debug.Log("[DesktopValidation] Rejected invalid " + mode + ": PASS");
                    }
                }
                finally
                {
                    connection?.Dispose();
                    if (texture != null) UnityEngine.Object.DestroyImmediate(texture);
                    if (!process.WaitForExit(2000)) process.Kill();
                    // Remove only the exact unique test directory created above.
                    Directory.Delete(temp, true);
                }
            }
        }
    }
}
