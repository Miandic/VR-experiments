#if UNITY_EDITOR
using System;
using UnityEngine;

namespace QuestDesktop
{
    public sealed partial class DesktopApp
    {
        // Exercises the actual interaction state machine with deterministic tracked-hand samples.
        public void VerifyHandInteractions()
        {
            var saved = JsonUtility.ToJson(display);
            var headPosition = head.position; var headRotation = head.rotation;
            bool previousMenu = menuVisible;
            void Check(bool value, string why) { if (!value) throw new Exception("Hand interaction: " + why); }
            HandFrame Sample(Vector3 p, bool pinch = false, bool grip = false) => new HandFrame {
                tracked = true, confident = true, pinch = pinch, grip = grip, indexTip = p, pinchPoint = p, palm = p,
                rotation = Quaternion.identity, ray = new Ray(p - Vector3.forward * 0.4f, Vector3.forward)
            };
            void Reset()
            {
                ProcessHand(0, default); ProcessHand(1, default);
                var away = Sample(new Vector3(-10, 10, -10)); away.ray = new Ray(away.indexTip, Vector3.back);
                ProcessHand(0, away); ProcessHand(1, away);
                SetMenu(false); display.hud = false; display.width = 1;
                display.worldPosition = Vector3.forward; display.worldEuler = Vector3.zero;
                head.position = Vector3.zero; head.rotation = Quaternion.identity;
                ApplyDisplay();
            }
            try
            {
                Reset();
                var edge = handTargets.Find(t => t.rect.name == "Edge 3");
                var start = edge.rect.position;
                ProcessHand(0, Sample(start)); ProcessHand(0, Sample(start, true));
                Check(handSessions[0].captured?.kind == HandTargetKind.Move, "direct pinch failed to capture edge");
                ProcessHand(0, Sample(start + new Vector3(0.2f, 0.1f, 0.12f), true));
                Check(Vector3.Distance(screen.position, new Vector3(0.2f, 0.1f, 1.12f)) < 0.01f, "direct move lost grab offset");
                ProcessHand(0, Sample(start));
                Check(manipulationOwner == -1, "release did not end move");

                Reset(); start = handTargets.Find(t => t.rect.name == "Corner 1").rect.position;
                Vector3 anchor = screen.position + new Vector3(-0.5f, -0.5f / Aspect, 0);
                ProcessHand(0, Sample(start)); ProcessHand(0, Sample(start, true));
                ProcessHand(0, Sample(start + new Vector3(0.2f, 0.2f / Aspect, 0), true));
                Check(Mathf.Abs(display.width - 1.2f) < 0.01f, "corner resize wrong width");
                Check(Vector3.Distance(screen.position - new Vector3(display.width / 2, display.width / Aspect / 2, 0), anchor) < 0.01f, "opposite corner moved");
                float fixedWidth = display.width;
                ProcessHand(0, default);
                Check(manipulationOwner == -1 && Mathf.Approximately(display.width, fixedWidth), "tracking loss did not safely release");
                ProcessHand(0, Sample(start, true));
                Check(manipulationOwner == -1, "reacquisition started a grab before release");

                Reset(); start = handTargets.Find(t => t.rect.name == "Corner 1").rect.position;
                var openFinger = Sample(start);
                openFinger.pinchPoint = start + new Vector3(0.1f, -0.08f, -0.05f);
                ProcessHand(0, openFinger);
                var closedFinger = Sample(start + new Vector3(0.09f, -0.07f, 0.06f), true);
                closedFinger.confident = false; // A real close pinch can temporarily reduce whole-hand confidence.
                ProcessHand(0, closedFinger);
                Check(handSessions[0].captured?.kind == HandTargetKind.Corner && handSessions[0].direct,
                    "closing the fingers lost the direct hover or changed it to a ray grab");
                Check(Mathf.Abs(display.width - 1) < 0.01f, "latched direct grab jumped on capture");

                Reset(); start = handTargets.Find(t => t.rect.name == "Corner 1").rect.position;
                var remote = Sample(new Vector3(0, -0.3f, 0.3f)); remote.ray = new Ray(Vector3.zero, start.normalized);
                ProcessHand(0, remote); remote.pinch = true; ProcessHand(0, remote);
                Check(handSessions[0].captured?.kind == HandTargetKind.Corner && !handSessions[0].direct, "ray pinch did not capture corner");
                remote.ray = new Ray(Vector3.zero, (start + new Vector3(0.2f, 0.2f / Aspect, 0)).normalized);
                ProcessHand(0, remote);
                Check(Mathf.Abs(display.width - 1.2f) < 0.01f, "ray resize failed");

                Reset(); start = handTargets.Find(t => t.rect.name == "Edge 3").rect.position;
                ProcessHand(0, Sample(start)); ProcessHand(0, Sample(start, false, true));
                Check(handSessions[0].gripCapture && manipulationOwner == 0, "closed hand did not grab edge");
                ProcessHand(1, Sample(start)); ProcessHand(1, Sample(start, true));
                Check(handSessions[1].captured == null, "two hands captured same screen transform");
                var rotated = Sample(start, false, true); rotated.rotation = Quaternion.Euler(0, 20, 0);
                ProcessHand(0, rotated);
                Check(Quaternion.Angle(screen.rotation, rotated.rotation) < 0.1f, "direct grab did not rotate screen with hand");

                Reset(); start = handTargets.Find(t => t.rect.name == "Edge 3").rect.position;
                var rayMove = Sample(new Vector3(0, -0.3f, 0.3f)); rayMove.ray = new Ray(Vector3.zero, start.normalized);
                ProcessHand(0, rayMove); rayMove.pinch = true; ProcessHand(0, rayMove);
                Check(handSessions[0].captured?.kind == HandTargetKind.Move && !handSessions[0].direct, "ray move did not capture edge");
                rayMove.ray = new Ray(Vector3.zero, (start + Vector3.right * 0.3f).normalized); ProcessHand(0, rayMove);
                Check(Vector3.Distance(screen.position, Vector3.forward) > 0.1f, "ray move did not move screen");

                Reset(); display.hud = true; display.hudWidth = 0.8f; display.hudDistance = 0.65f;
                display.hudX = display.hudY = 0; ApplyDisplay();
                start = handTargets.Find(t => t.rect.name == "Edge 3").rect.position;
                ProcessHand(0, Sample(start)); ProcessHand(0, Sample(start, true));
                head.position += Vector3.forward * 0.1f;
                ProcessHand(0, Sample(start + new Vector3(0.1f, 0.05f, 0.1f), true));
                Check(Mathf.Abs(display.hudX - 0.1f) < 0.01f && Mathf.Abs(display.hudY - 0.05f) < 0.01f
                    && screen.parent == head, "HUD drag did not preserve head-relative coordinates");

                Reset(); var menuTarget = handTargets.Find(t => t.rect.name == "МЕНЮ");
                int previousPokes = pokeActionCount;
                var approach = menuTarget.rect.position - menuTarget.rect.forward * 0.03f;
                ProcessHand(0, Sample(approach)); ProcessHand(0, Sample(menuTarget.rect.position));
                Check(menuVisible && pokeActionCount == previousPokes + 1, "finger poke failed");
                ProcessHand(0, Sample(menuTarget.rect.position));
                Check(menuVisible && pokeActionCount == previousPokes + 1, "held poke repeated click");

                Reset(); menuTarget = handTargets.Find(t => t.rect.name == "МЕНЮ");
                previousPokes = pokeActionCount;
                ProcessHand(0, Sample(menuTarget.rect.position - menuTarget.rect.forward * 0.06f));
                var skippedContact = Sample(menuTarget.rect.position + menuTarget.rect.forward * 0.07f);
                skippedContact.confident = false;
                ProcessHand(0, skippedContact);
                Check(menuVisible && pokeActionCount == previousPokes + 1, "fast poke crossing between samples was missed");

                Reset(); var slider = handTargets.Find(t => t.kind == HandTargetKind.Brightness);
                Vector3 SliderPoint(float value, float depth) => slider.rect.TransformPoint(new Vector3(0, slider.rect.rect.yMin + slider.rect.rect.height * value, depth / slider.rect.lossyScale.x));
                ProcessHand(0, Sample(SliderPoint(0.2f, -0.03f)));
                ProcessHand(0, Sample(SliderPoint(0.2f, -0.002f)));
                Check(handSessions[0].touchCapture, "finger contact did not capture slider");
                ProcessHand(0, Sample(SliderPoint(0.8f, -0.002f)));
                Check(Mathf.Abs(display.brightness - 0.82f) < 0.015f, "brightness slider did not follow finger");
                ProcessHand(0, Sample(SliderPoint(0.8f, -0.08f)));
                Check(handSessions[0].captured == null, "slider did not release after withdrawal");

                Reset(); display.hud = true; display.hudDistance = 1; display.hudX = display.hudY = 0; ApplyDisplay();
                slider = handTargets.Find(t => t.kind == HandTargetKind.Opacity);
                var raySample = Sample(new Vector3(-0.2f, -0.3f, 0.3f));
                Vector3 targetPoint = slider.rect.TransformPoint(new Vector3(0, slider.rect.rect.yMin, 0));
                raySample.ray = new Ray(head.position, (targetPoint - head.position).normalized);
                ProcessHand(0, raySample); raySample.pinch = true; ProcessHand(0, raySample);
                Check(handSessions[0].captured?.kind == HandTargetKind.Opacity, "ray did not capture HUD opacity");
                Check(display.hudOpacity < 0.01f, "HUD opacity slider cannot reach zero");
                targetPoint = slider.rect.TransformPoint(new Vector3(0, slider.rect.rect.yMax, 0));
                raySample.ray = new Ray(head.position, targetPoint.normalized); ProcessHand(0, raySample);
                Check(display.hudOpacity > 0.99f, "HUD opacity slider cannot reach one");
                Check(sliderPanel.gameObject.activeInHierarchy && toolPanel.gameObject.activeInHierarchy, "transparent HUD hid controls");
                Debug.Log("[DesktopHands] PASS: direct move/grip, anchored aspect resize, ray resize, poke debounce, finger slider, ray alpha 0..1, tracking loss and exclusive ownership");
            }
            finally
            {
                CancelHandCaptures(); display = JsonUtility.FromJson<DisplayConfig>(saved);
                head.position = headPosition; head.rotation = headRotation;
                ApplyDisplay(); SetMenu(previousMenu);
            }
        }
    }
}
#endif
