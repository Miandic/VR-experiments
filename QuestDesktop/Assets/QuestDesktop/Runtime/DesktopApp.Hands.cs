using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace QuestDesktop
{
    public sealed partial class DesktopApp
    {
        enum HandTargetKind { Button, Move, Corner, Brightness, Opacity }
        sealed class HandTarget
        {
            public RectTransform rect;
            public Image image;
            public HandTargetKind kind;
            public Action click;
            public Vector2 corner;
            public bool side;
        }
        sealed class HandSession
        {
            public HandFrame frame;
            public bool wasPinch, wasGrip, requireRelease, direct, gripCapture, touchCapture, hoverWasDirect;
            public HandTarget captured, pokeArmed, hovered;
            public float hoverTime, palmSince = -1, capturedAt, depth;
            public Vector3 initialPointer, initialPinch, initialCenter, opposite, cornerPoint;
            public Quaternion initialRotation, initialHandRotation;
            public Plane plane;
            public Transform parent;
        }
        readonly List<HandTarget> handTargets = new List<HandTarget>();
        readonly HandSession[] handSessions = { new HandSession(), new HandSession() };
        readonly LineRenderer[] handRays = new LineRenderer[2];
        readonly Transform[] fingerDots = new Transform[2], rayDots = new Transform[2];
        HandInputSource handInput;
        RectTransform handChrome, sliderPanel, toolPanel;
        CanvasGroup chromeGroup, sideGroup;
        Text brightnessValue, opacityValue, handHint;
        RectTransform brightnessThumb, opacityThumb;
        float chromeUntil, sideUntil, nextDisplaySave;
        bool handDisplayDirty;
        int manipulationOwner = -1;
        string lastHandAction = "";
        int handActionCount, directActionCount, rayActionCount, pokeActionCount;
        Mesh cursorMesh;
        float ScreenScale => screen != null ? screen.lossyScale.x : 0.001f;
        float Aspect => texture != null && ImageWidth > 2 ? (float)ImageWidth / ImageHeight : 16f / 9f;

        void BuildHandControls(OVRCameraRig rig)
        {
            handInput = new HandInputSource(rig != null ? rig.trackingSpace : head.parent, head);
            handChrome = Rect("Screen affordances", screen, 0, 0, 1600, 900);
            handChrome.anchorMin = handChrome.anchorMax = handChrome.pivot = new Vector2(0.5f, 0.5f);
            handChrome.anchoredPosition = Vector2.zero;
            chromeGroup = handChrome.gameObject.AddComponent<CanvasGroup>(); chromeGroup.alpha = 0.2f;
            // Corners take priority over the edge strips at the same location.
            for (int i = 0; i < 4; i++)
            {
                var sign = new Vector2(i % 2 == 0 ? -1 : 1, i < 2 ? 1 : -1);
                var target = NewHandTarget("Corner " + i, handChrome, HandTargetKind.Corner);
                target.corner = sign;
                var mark = Label(target.rect, sign.y > 0 ? sign.x > 0 ? "↗" : "↖" : sign.x > 0 ? "↘" : "↙", 0, 0, 50, 50, 34);
                mark.alignment = TextAnchor.MiddleCenter;
            }
            for (int i = 0; i < 4; i++) NewHandTarget("Edge " + i, handChrome, HandTargetKind.Move);

            sliderPanel = Rect("Side adjustments", screen, 0, 0, 200, 400);
            sliderPanel.anchorMin = sliderPanel.anchorMax = sliderPanel.pivot = new Vector2(0.5f, 0.5f);
            sideGroup = sliderPanel.gameObject.AddComponent<CanvasGroup>(); sideGroup.alpha = 0;
            sliderPanel.gameObject.AddComponent<Image>().color = new Color(0.025f, 0.045f, 0.065f, 0.95f);
            var brightness = NewHandTarget("Brightness slider", sliderPanel, HandTargetKind.Brightness); brightness.side = true;
            var opacity = NewHandTarget("Opacity slider", sliderPanel, HandTargetKind.Opacity); opacity.side = true;
            brightnessThumb = NewSliderThumb(brightness.rect);
            opacityThumb = NewSliderThumb(opacity.rect);
            brightnessValue = Label(sliderPanel, "", 0, 0, 150, 55, 22);
            opacityValue = Label(sliderPanel, "", 0, 0, 150, 55, 22);

            toolPanel = Rect("Screen toolbar", screen, 0, 0, 700, 70);
            toolPanel.anchorMin = toolPanel.anchorMax = toolPanel.pivot = new Vector2(0.5f, 0.5f);
            NewTool("HUD / МИР", 0, () => { CancelHandCaptures(); display.hud = !display.hud; Change(); });
            NewTool("РЯДОМ", 1, BringNear);
            NewTool("МЕНЮ", 2, () => SetMenu(!menuVisible));
            handHint = Label(handChrome, "Край — переместить   •   Угол — размер   •   Справа — яркость", 0, 0, 1500, 60, 23);
            handHint.alignment = TextAnchor.MiddleCenter;
            for (int h = 0; h < 2; h++)
            {
                var ray = new GameObject("Hand pointer " + h).AddComponent<LineRenderer>();
                ray.positionCount = 2; ray.startWidth = 0.0018f; ray.endWidth = 0.0007f;
                ray.material = new Material(Shader.Find("Sprites/Default"));
                ray.startColor = new Color(0.3f, 0.8f, 1, 0.15f); ray.endColor = Color.cyan;
                ray.enabled = false; handRays[h] = ray;
                fingerDots[h] = NewDot("Index cursor " + h, 0.007f);
                rayDots[h] = NewDot("Pointer cursor " + h, 0.01f);
            }
            LayoutHandControls();
        }

        Transform NewDot(string name, float size)
        {
            if (cursorMesh == null)
            {
                // Explicit rendering components avoid CreatePrimitive's stripped collider dependency on IL2CPP.
                cursorMesh = new Mesh { name = "Hand cursor octahedron" };
                cursorMesh.vertices = new[] { Vector3.right * 0.5f, Vector3.left * 0.5f, Vector3.up * 0.5f,
                    Vector3.down * 0.5f, Vector3.forward * 0.5f, Vector3.back * 0.5f };
                cursorMesh.triangles = new[] { 2, 0, 4, 2, 4, 1, 2, 1, 5, 2, 5, 0, 3, 4, 0, 3, 1, 4, 3, 5, 1, 3, 0, 5 };
                cursorMesh.colors = new[] { Color.white, Color.white, Color.white, Color.white, Color.white, Color.white };
                cursorMesh.RecalculateNormals(); cursorMesh.RecalculateBounds();
            }
            var dot = new GameObject(name, typeof(MeshFilter), typeof(MeshRenderer));
            dot.GetComponent<MeshFilter>().sharedMesh = cursorMesh;
            dot.transform.localScale = Vector3.one * size;
            dot.GetComponent<Renderer>().material = new Material(Shader.Find("Sprites/Default")) { color = Color.cyan };
            dot.SetActive(false); return dot.transform;
        }
        HandTarget NewHandTarget(string name, RectTransform parent, HandTargetKind kind)
        {
            var rect = Rect(name, parent, 0, 0, 50, 50);
            rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0.5f, 0.5f);
            var image = rect.gameObject.AddComponent<Image>(); image.color = new Color(0.2f, 0.65f, 0.85f, 0.6f);
            var target = new HandTarget { rect = rect, image = image, kind = kind };
            handTargets.Add(target); return target;
        }
        void NewTool(string title, int index, Action click)
        {
            var target = NewHandTarget(title, toolPanel, HandTargetKind.Button); target.click = click;
            target.corner = new Vector2(index, 0); // Toolbar index only.
            var text = Label(target.rect, title, 0, 0, 200, 70, 24); text.alignment = TextAnchor.MiddleCenter;
        }
        RectTransform NewSliderThumb(RectTransform rail)
        {
            var thumb = Rect("Thumb", rail, 0, 0, 80, 30);
            thumb.anchorMin = thumb.anchorMax = thumb.pivot = new Vector2(0.5f, 0.5f);
            thumb.gameObject.AddComponent<Image>().color = new Color(0.7f, 0.95f, 1, 1); return thumb;
        }
        static void CenterRect(RectTransform rect, Vector2 position, Vector2 size)
        {
            rect.anchoredPosition = position; rect.sizeDelta = size;
            rect.localPosition = new Vector3(rect.localPosition.x, rect.localPosition.y, -2);
        }
        void LayoutHandControls()
        {
            if (handChrome == null) return;
            float unit = 1 / ScreenScale, w = 1600, height = w / Aspect;
            handChrome.sizeDelta = screen.sizeDelta;
            foreach (var target in handTargets)
            {
                if (target.kind == HandTargetKind.Corner)
                {
                    CenterRect(target.rect, new Vector2(target.corner.x * w / 2, target.corner.y * height / 2), Vector2.one * 0.042f * unit);
                }
                else if (target.kind == HandTargetKind.Move)
                {
                    int i = int.Parse(target.rect.name.Substring(5));
                    bool horizontal = i < 2;
                    CenterRect(target.rect, horizontal ? new Vector2(0, (i == 0 ? 1 : -1) * height / 2)
                        : new Vector2((i == 2 ? -1 : 1) * w / 2, 0),
                        horizontal ? new Vector2(w - 0.06f * unit, 0.018f * unit) : new Vector2(0.018f * unit, height - 0.06f * unit));
                }
            }
            CenterRect(sliderPanel, new Vector2(w / 2 + 0.14f * unit, 0), new Vector2(0.25f, 0.4f) * unit);
            foreach (var target in handTargets)
            {
                if (!target.side) continue;
                bool bright = target.kind == HandTargetKind.Brightness;
                CenterRect(target.rect, new Vector2(bright ? -0.06f : 0.06f, 0.01f) * unit, new Vector2(0.035f, 0.23f) * unit);
                var thumb = bright ? brightnessThumb : opacityThumb;
                float value = bright ? Mathf.InverseLerp(0.1f, 1, display.brightness) : display.hudOpacity;
                CenterRect(thumb, new Vector2(0, (value - 0.5f) * target.rect.sizeDelta.y), new Vector2(0.055f, 0.016f) * unit);
                var label = bright ? brightnessValue : opacityValue;
                label.rectTransform.anchorMin = label.rectTransform.anchorMax = label.rectTransform.pivot = new Vector2(0.5f, 0.5f);
                CenterRect(label.rectTransform, new Vector2(bright ? -0.06f : 0.06f, -0.15f) * unit, new Vector2(0.118f, 0.07f) * unit);
                label.fontSize = Mathf.RoundToInt(0.012f * unit);
                label.alignment = TextAnchor.MiddleCenter;
                label.text = bright ? $"ЯРКОСТЬ\n{display.brightness:P0}" : $"HUD АЛЬФА\n{display.hudOpacity:P0}";
                target.image.color = !bright && !display.hud ? new Color(0.3f, 0.35f, 0.4f, 0.4f) : new Color(0.15f, 0.5f, 0.65f, 0.9f);
            }
            CenterRect(toolPanel, new Vector2(0, height / 2 + 0.048f * unit), new Vector2(0.51f, 0.055f) * unit);
            foreach (var target in handTargets)
            {
                if (target.rect.parent != toolPanel) continue;
                CenterRect(target.rect, new Vector2((target.corner.x - 1) * 0.17f * unit, 0), new Vector2(0.16f, 0.045f) * unit);
                var text = target.rect.GetComponentInChildren<Text>();
                text.rectTransform.anchorMin = text.rectTransform.anchorMax = text.rectTransform.pivot = new Vector2(0.5f, 0.5f);
                CenterRect(text.rectTransform, Vector2.zero, target.rect.sizeDelta);
                text.fontSize = Mathf.RoundToInt(0.014f * unit);
            }
            handHint.rectTransform.anchorMin = handHint.rectTransform.anchorMax = handHint.rectTransform.pivot = new Vector2(0.5f, 0.5f);
            CenterRect(handHint.rectTransform, new Vector2(0, -height / 2 - 0.035f * unit), new Vector2(w, 0.035f * unit));
            handHint.fontSize = Mathf.RoundToInt(0.014f * unit);
        }

        void BringNear()
        {
            CancelHandCaptures();
            if (display.hud) { display.hudDistance = 0.5f; display.hudX = 0; display.hudY = -0.12f; display.hudWidth = Mathf.Min(display.hudWidth, 0.65f); }
            else { display.worldPosition = head.TransformPoint(new Vector3(0, -0.12f, 0.5f)); display.worldEuler = head.eulerAngles; display.width = Mathf.Min(display.width, 0.65f); }
            Change();
        }

        void LateUpdate()
        {
            if (handInput == null || screen == null) return;
            foreach (var target in handTargets)
                target.image.color = target.side ? target.kind == HandTargetKind.Opacity && !display.hud
                    ? new Color(0.3f, 0.35f, 0.4f, 0.4f) : new Color(0.15f, 0.5f, 0.65f, 0.9f)
                    : target.kind == HandTargetKind.Button ? normal : new Color(0.2f, 0.65f, 0.85f, 0.65f);
            foreach (var button in buttons) button.image.color = normal;
            for (int h = 0; h < 2; h++) ProcessHand(h, handInput.Read(h));
            float now = Time.unscaledTime;
            chromeGroup.alpha = Mathf.MoveTowards(chromeGroup.alpha, now < chromeUntil || manipulationOwner >= 0 ? 1 : 0.15f, Time.unscaledDeltaTime * 5);
            sideGroup.alpha = Mathf.MoveTowards(sideGroup.alpha, now < sideUntil ? 1 : 0, Time.unscaledDeltaTime * 6);
            if (handDisplayDirty && now >= nextDisplaySave && manipulationOwner < 0 && handSessions[0].captured == null && handSessions[1].captured == null)
            {
                handDisplayDirty = false;
                try { ConfigFiles.Save("display.json", display); } catch (Exception error) { message = error.Message; }
            }
        }

        static bool Inside(RectTransform rect, Vector3 point, float paddingMeters = 0)
        {
            var p = rect.InverseTransformPoint(point);
            float padding = paddingMeters / rect.lossyScale.x;
            var bounds = rect.rect; bounds.xMin -= padding; bounds.xMax += padding; bounds.yMin -= padding; bounds.yMax += padding;
            return bounds.Contains(new Vector2(p.x, p.y));
        }
        static float Depth(RectTransform rect, Vector3 point) => Vector3.Dot(point - rect.position, rect.forward);
        IEnumerable<HandTarget> AvailableTargets()
        {
            // Menus are modal, so a hand cannot grab the screen through a menu button.
            if (menuVisible)
            {
                foreach (var button in buttons)
                    if (button.rect != null && button.rect.gameObject.activeInHierarchy)
                        yield return new HandTarget { rect = button.rect, image = button.image, kind = HandTargetKind.Button, click = button.click };
            }
            foreach (var target in handTargets)
                if (target.rect.gameObject.activeInHierarchy && (target.kind != HandTargetKind.Opacity || display.hud)) yield return target;
        }
        HandTarget PickDirect(HandFrame frame, out Vector3 point)
        {
            point = frame.indexTip;
            HandTarget bestTarget = null;
            float best = float.PositiveInfinity;
            foreach (var target in AvailableTargets())
            {
                bool grab = target.kind == HandTargetKind.Move || target.kind == HandTargetKind.Corner;
                // Hover with the index finger before closing the pinch. Closing moves the fingertip
                // and midpoint by several centimetres, so testing only the midpoint loses the edge.
                var candidate = frame.indexTip;
                float distance = RectDistance(target.rect, candidate);
                if (grab && RectDistance(target.rect, frame.pinchPoint) < distance)
                { candidate = frame.pinchPoint; distance = RectDistance(target.rect, candidate); }
                if (grab && frame.grip && RectDistance(target.rect, frame.palm) < distance)
                { candidate = frame.palm; distance = RectDistance(target.rect, candidate); }
                float depth = Depth(target.rect, candidate);
                if (Mathf.Abs(depth) > 0.12f || !Inside(target.rect, candidate, grab ? 0.025f : 0.015f)) continue;
                if (menuVisible && target.rect.IsChildOf(screen) && Inside(menu, candidate)
                    && Mathf.Abs(Depth(menu, candidate)) < 0.1f) continue;
                float score = distance - (target.kind == HandTargetKind.Corner ? 0.012f : 0);
                if (score >= best) continue;
                point = candidate; best = score; bestTarget = target;
            }
            return bestTarget;
        }
        static float RectDistance(RectTransform rect, Vector3 point)
        {
            var local = rect.InverseTransformPoint(point);
            return Vector3.Distance(point, rect.TransformPoint(new Vector3(
                Mathf.Clamp(local.x, rect.rect.xMin, rect.rect.xMax), Mathf.Clamp(local.y, rect.rect.yMin, rect.rect.yMax), 0)));
        }
        HandTarget PickRay(HandFrame frame, out Vector3 point)
        {
            HandTarget found = null; point = frame.ray.GetPoint(1);
            float best = 10;
            foreach (var target in AvailableTargets())
            {
                if (!ScreenGeometry.Intersect(frame.ray, target.rect.position, target.rect.forward, out var candidate)
                    || !Inside(target.rect, candidate, 0.008f)) continue;
                float distance = Vector3.Distance(frame.ray.origin, candidate);
                if (found != null && distance > best + 0.008f) continue;
                // Keep the first target when corner and border overlap in the same plane.
                if (found != null && Mathf.Abs(distance - best) < 0.008f) continue;
                found = target; point = candidate; best = distance;
            }
            return found;
        }
        void Reveal(HandFrame frame)
        {
            var local = screen.InverseTransformPoint(frame.indexTip) * ScreenScale;
            float half = screen.rect.width * ScreenScale / 2;
            float halfHeight = half / Aspect;
            if (Mathf.Abs(local.z) < 0.25f && Mathf.Abs(local.x) < half + 0.3f && Mathf.Abs(local.y) < halfHeight + 0.15f)
                chromeUntil = Time.unscaledTime + 1.3f;
            bool nearSide = Mathf.Abs(local.z) < 0.25f && local.x > half - 0.08f && local.x < half + 0.35f && Mathf.Abs(local.y) < 0.3f;
            bool raySide = ScreenGeometry.Intersect(frame.ray, screen.position, screen.forward, out var hit)
                && screen.InverseTransformPoint(hit).x * ScreenScale > half - 0.025f
                && Inside(sliderPanel, hit, 0.04f);
            if (nearSide || raySide || handSessions[0].captured?.side == true || handSessions[1].captured?.side == true)
                sideUntil = Time.unscaledTime + 1.5f;
        }

        void ProcessHand(int h, HandFrame frame)
        {
            var session = handSessions[h]; var previousFrame = session.frame; session.frame = frame;
            if (fingerDots[h] != null) fingerDots[h].gameObject.SetActive(false);
            if (rayDots[h] != null) rayDots[h].gameObject.SetActive(false);
            if (handRays[h] != null) handRays[h].enabled = false;
            if (!frame.tracked || frame.systemGesture)
            {
                EndCapture(h); session.requireRelease = true; session.wasPinch = frame.pinch; session.wasGrip = frame.grip;
                session.pokeArmed = session.hovered = null; session.palmSince = -1; return;
            }
            Reveal(frame);
            if (pokeRelease[h])
            {
                if (session.pokeArmed?.rect != null && Depth(session.pokeArmed.rect, frame.indexTip) > -0.025f
                    && Inside(session.pokeArmed.rect, frame.indexTip, 0.02f)) return;
                pokeRelease[h] = false; session.pokeArmed = null;
            }
            if (session.requireRelease)
            {
                if (frame.pinch || frame.grip) return;
                session.requireRelease = false; session.wasPinch = session.wasGrip = false;
            }
            bool pinchDown = frame.pinch && !session.wasPinch, gripDown = frame.grip && !session.wasGrip;
            session.wasPinch = frame.pinch; session.wasGrip = frame.grip;
            if (session.captured != null)
            {
                bool held = session.touchCapture ? Mathf.Abs(Depth(session.captured.rect, frame.indexTip)) < 0.045f
                    : session.gripCapture ? frame.grip : frame.pinch;
                if (!held) EndCapture(h);
                else UpdateCapture(h, frame);
                return;
            }
            var target = PickDirect(frame, out var point);
            bool direct = target != null;
            if (!direct && (pinchDown || gripDown) && session.hoverWasDirect && session.hovered?.rect != null
                && session.hovered.rect.gameObject.activeInHierarchy && Time.unscaledTime - session.hoverTime < 0.3f
                && Mathf.Min(RectDistance(session.hovered.rect, frame.indexTip), RectDistance(session.hovered.rect, frame.pinchPoint)) < 0.17f)
            { target = session.hovered; direct = true; point = frame.pinchPoint; }
            if (!frame.pinch && !frame.grip && previousFrame.tracked && !previousFrame.pinch
                && Vector3.Distance(previousFrame.indexTip, frame.indexTip) < 0.25f && TrySweptPoke(h, previousFrame.indexTip, frame.indexTip)) return;
            if (!direct) target = PickRay(frame, out point);
            if (target == null && pinchDown && session.hovered?.rect != null && session.hovered.rect.gameObject.activeInHierarchy
                && Time.unscaledTime - session.hoverTime < 0.15f)
            {
                target = session.hovered;
                if (!ScreenGeometry.Intersect(frame.ray, target.rect.position, target.rect.forward, out point)) target = null;
            }
            if (target != null) { session.hovered = target; session.hoverWasDirect = direct; session.hoverTime = Time.unscaledTime; }
            if (target != null)
            {
                chromeUntil = Time.unscaledTime + 1;
                if (target.side) sideUntil = Time.unscaledTime + 1.5f;
                target.image.color = new Color(0.2f, 0.8f, 0.95f, 1);
                if (direct && fingerDots[h] != null)
                {
                    fingerDots[h].gameObject.SetActive(true); fingerDots[h].position = frame.indexTip;
                }
                else if (!direct && handRays[h] != null)
                {
                    handRays[h].enabled = true; handRays[h].SetPosition(0, frame.ray.origin); handRays[h].SetPosition(1, point);
                    rayDots[h].gameObject.SetActive(true); rayDots[h].position = point - frame.ray.direction * 0.004f;
                }
                if (pinchDown && (frame.confident || direct))
                {
                    if (target.kind == HandTargetKind.Button) Activate(h, target, direct ? "direct pinch" : "ray pinch");
                    else BeginCapture(h, target, frame, point, direct, false, false);
                }
                else if (direct && gripDown && (target.kind == HandTargetKind.Move || target.kind == HandTargetKind.Corner))
                    BeginCapture(h, target, frame, point, true, true, false);
                else if (direct && target.kind == HandTargetKind.Button)
                {
                    float depth = Depth(target.rect, frame.indexTip);
                    if (depth < -0.012f) session.pokeArmed = target;
                    if (SameTarget(session.pokeArmed, target) && depth >= -0.004f && depth < 0.1f && !frame.pinch && !frame.grip)
                    {
                        session.pokeArmed = null; Activate(h, target, "poke");
                        session.requireRelease = true; session.pokeArmed = target; pokeRelease[h] = true;
                    }
                }
                else if (direct && (target.kind == HandTargetKind.Brightness || target.kind == HandTargetKind.Opacity)
                    && Depth(target.rect, frame.indexTip) >= -0.018f && Depth(target.rect, frame.indexTip) < 0.06f)
                    BeginCapture(h, target, frame, point, true, false, true);
            }
            else session.pokeArmed = null;
            // Palm-to-face + pinch opens settings without a controller, even when the screen is out of reach.
            if (frame.palmFacingHead && target == null)
            {
                if (session.palmSince < 0) session.palmSince = Time.unscaledTime;
                if (pinchDown && Time.unscaledTime - session.palmSince > 0.3f) { SetMenu(!menuVisible); session.palmSince = -1; }
            }
            else session.palmSince = -1;
        }
        readonly bool[] pokeRelease = new bool[2];
        bool TrySweptPoke(int h, Vector3 before, Vector3 after)
        {
            foreach (var target in AvailableTargets())
            {
                if (target.kind != HandTargetKind.Button) continue;
                float a = Depth(target.rect, before), b = Depth(target.rect, after);
                if (a >= -0.006f || b < -0.006f || a < -0.15f || b > 0.12f) continue;
                var contact = Vector3.Lerp(before, after, (-0.006f - a) / (b - a));
                if (!Inside(target.rect, contact, 0.008f)) continue;
                Activate(h, target, "poke");
                handSessions[h].pokeArmed = target; pokeRelease[h] = true;
                return true;
            }
            return false;
        }
        static bool SameTarget(HandTarget a, HandTarget b) => a != null && b != null && a.rect == b.rect;
        void Activate(int hand, HandTarget target, string mode)
        {
            if (mode == "poke" && pokeRelease[hand]) return;
            RecordHandAction(mode + ": " + target.rect.name, mode.StartsWith("ray"), mode == "poke");
            target.click?.Invoke();
        }
        void RecordHandAction(string action, bool ray, bool poke = false)
        {
            lastHandAction = action; handActionCount++;
            if (ray) rayActionCount++; else directActionCount++;
            if (poke) pokeActionCount++;
            Debug.Log("[QuestDesktop Hands] " + action);
        }
        Vector3 InParent(HandSession session, Vector3 point) => session.parent != null ? session.parent.InverseTransformPoint(point) : point;
        Vector3 ToWorld(HandSession session, Vector3 point) => session.parent != null ? session.parent.TransformPoint(point) : point;
        Quaternion ParentRotation(HandSession session) => session.parent != null ? session.parent.rotation : Quaternion.identity;

        void BeginCapture(int h, HandTarget target, HandFrame frame, Vector3 point, bool direct, bool grip, bool touch)
        {
            bool transformsScreen = target.kind == HandTargetKind.Move || target.kind == HandTargetKind.Corner;
            if (transformsScreen && manipulationOwner >= 0) return;
            if (SameTarget(handSessions[1 - h].captured, target)) return;
            var session = handSessions[h];
            session.captured = target; session.direct = direct; session.gripCapture = grip; session.touchCapture = touch;
            session.parent = display.hud ? head : null;
            session.initialCenter = InParent(session, screen.position);
            session.initialRotation = Quaternion.Inverse(ParentRotation(session)) * screen.rotation;
            session.initialHandRotation = Quaternion.Inverse(ParentRotation(session)) * frame.rotation;
            session.initialPinch = InParent(session, frame.pinchPoint);
            session.initialPointer = InParent(session, direct ? (grip ? frame.palm : frame.pinchPoint) : point);
            session.depth = Vector3.Distance(frame.ray.origin, point);
            session.plane = new Plane(session.initialRotation * Vector3.forward, session.initialCenter);
            float width = display.hud ? display.hudWidth : display.width;
            session.cornerPoint = session.initialCenter + session.initialRotation * new Vector3(target.corner.x * width / 2, target.corner.y * width / Aspect / 2, 0);
            session.opposite = session.initialCenter * 2 - session.cornerPoint;
            session.capturedAt = Time.unscaledTime;
            if (transformsScreen) manipulationOwner = h;
            RecordHandAction((direct ? touch ? "touch" : grip ? "grip" : "direct pinch" : "ray pinch") + ": " + target.rect.name, !direct);
            UpdateCapture(h, frame);
        }
        void UpdateCapture(int h, HandFrame frame)
        {
            var session = handSessions[h]; var target = session.captured;
            target.image.color = new Color(1, 0.75f, 0.2f, 1);
            chromeUntil = sideUntil = Time.unscaledTime + 1;
            Vector3 point;
            if (session.direct) point = InParent(session, session.touchCapture ? frame.indexTip : session.gripCapture ? frame.palm : frame.pinchPoint);
            else
            {
                var ray = new Ray(InParent(session, frame.ray.origin), Quaternion.Inverse(ParentRotation(session)) * frame.ray.direction);
                if (target.kind == HandTargetKind.Move)
                {
                    float depthDelta = Vector3.Dot(InParent(session, frame.pinchPoint) - session.initialPinch, Quaternion.Inverse(ParentRotation(session)) * head.forward) * 3;
                    point = ray.GetPoint(Mathf.Clamp(session.depth + depthDelta, 0.2f, 8));
                }
                else
                {
                    var plane = target.side ? new Plane(Quaternion.Inverse(ParentRotation(session)) * target.rect.forward, InParent(session, target.rect.position)) : session.plane;
                    if (!plane.Raycast(ray, out float distance) || distance > 10) return;
                    point = ray.GetPoint(distance);
                }
                if (handRays[h] != null)
                {
                    handRays[h].enabled = true; handRays[h].SetPosition(0, frame.ray.origin); handRays[h].SetPosition(1, ToWorld(session, point));
                    rayDots[h].gameObject.SetActive(true); rayDots[h].position = ToWorld(session, point);
                }
            }
            if (target.kind == HandTargetKind.Brightness || target.kind == HandTargetKind.Opacity)
            {
                Vector3 local = target.rect.InverseTransformPoint(ToWorld(session, point));
                float value = Mathf.Clamp01((local.y - target.rect.rect.yMin) / target.rect.rect.height);
                if (target.kind == HandTargetKind.Brightness) display.brightness = Mathf.Lerp(0.1f, 1, value);
                else display.hudOpacity = value;
            }
            else if (target.kind == HandTargetKind.Move)
            {
                Quaternion rotation = session.initialRotation;
                if (session.direct && !display.hud)
                    rotation = Quaternion.Inverse(ParentRotation(session)) * frame.rotation * Quaternion.Inverse(session.initialHandRotation) * session.initialRotation;
                Vector3 offset = Quaternion.Inverse(session.initialRotation) * (session.initialCenter - session.initialPointer);
                Vector3 center = point + rotation * offset;
                SetManipulatedPose(session, center, rotation);
            }
            else if (target.kind == HandTargetKind.Corner)
            {
                Vector3 requested = session.cornerPoint + point - session.initialPointer;
                Vector3 diagonal = Quaternion.Inverse(session.initialRotation) * (requested - session.opposite);
                float width = ScreenGeometry.CornerWidth(new Vector2(diagonal.x, diagonal.y), target.corner, Aspect,
                    display.hud ? 0.2f : 0.25f, display.hud ? 2.5f : 6);
                Vector3 center = session.opposite + session.initialRotation * new Vector3(target.corner.x * width / 2, target.corner.y * width / Aspect / 2, 0);
                if (display.hud) display.hudWidth = width; else display.width = width;
                SetManipulatedPose(session, center, session.initialRotation);
            }
            display.Clamp(); ApplyDisplay(); handDisplayDirty = true; nextDisplaySave = Time.unscaledTime + 0.25f;
        }
        void SetManipulatedPose(HandSession session, Vector3 center, Quaternion rotation)
        {
            if (display.hud) { display.hudX = center.x; display.hudY = center.y; display.hudDistance = center.z; }
            else { display.worldPosition = ToWorld(session, center); display.worldEuler = (ParentRotation(session) * rotation).eulerAngles; }
        }
        void EndCapture(int h)
        {
            var session = handSessions[h];
            if (session.captured != null)
            {
                session.captured = null; handDisplayDirty = true; nextDisplaySave = Time.unscaledTime;
                if (manipulationOwner == h) manipulationOwner = -1;
            }
        }
        void CancelHandCaptures() { EndCapture(0); EndCapture(1); }
        void DisposeHandControls()
        {
            for (int h = 0; h < 2; h++)
            {
                if (handRays[h] != null) { Destroy(handRays[h].material); Destroy(handRays[h].gameObject); }
                foreach (var dot in new[] { fingerDots[h], rayDots[h] })
                    if (dot != null) { Destroy(dot.GetComponent<Renderer>().material); Destroy(dot.gameObject); }
            }
            if (cursorMesh != null) Destroy(cursorMesh);
        }
    }
}
