using UnityEngine;

namespace QuestDesktop
{
    public struct HandFrame
    {
        public bool tracked, confident, pinch, grip, systemGesture;
        public Vector3 indexTip, pinchPoint, palm;
        public Quaternion rotation;
        public Ray ray;
        public bool palmFacingHead;
    }

    // UI input only. No data collection, recording or connection to the original application.
    public sealed class HandInputSource
    {
        readonly OVRPlugin.HandState[] states = { new OVRPlugin.HandState(), new OVRPlugin.HandState() };
        readonly bool[] pinching = new bool[2], gripping = new bool[2];
        readonly Transform space, head;
        public HandInputSource(Transform trackingSpace, Transform eye) { space = trackingSpace; head = eye; }

        Vector3 Joint(OVRPlugin.HandState state, int index)
        {
            var value = state.BonePositions[index];
            return space.TransformPoint(new Vector3(value.x, value.y, -value.z));
        }

        public HandFrame Read(int hand)
        {
            var frame = new HandFrame();
            if (space == null || !OVRPlugin.GetHandState(OVRPlugin.Step.Render,
                hand == 0 ? OVRPlugin.Hand.HandLeft : OVRPlugin.Hand.HandRight, ref states[hand]))
            { pinching[hand] = gripping[hand] = false; return frame; }
            var state = states[hand];
            frame.systemGesture = (state.Status & OVRPlugin.HandStatus.SystemGestureInProgress) != 0;
            frame.tracked = (state.Status & OVRPlugin.HandStatus.HandTracked) != 0
                && state.BonePositions != null && state.BonePositions.Length >= 26
                && state.BoneRotations != null && state.BoneRotations.Length >= 26;
            if (!frame.tracked) { pinching[hand] = gripping[hand] = false; return frame; }
            frame.confident = state.HandConfidence == OVRPlugin.TrackingConfidence.High;
            float scale = state.HandScale > 0.5f && state.HandScale < 2 ? state.HandScale : 1;
            frame.indexTip = Joint(state, 10);
            frame.pinchPoint = (frame.indexTip + Joint(state, 5)) * 0.5f;
            frame.palm = Joint(state, 0);
            var palmPose = new OVRPlugin.Posef { Position = state.BonePositions[0], Orientation = state.BoneRotations[0] }.ToOVRPose();
            frame.rotation = space.rotation * palmPose.orientation;
            frame.palmFacingHead = Vector3.Dot(frame.rotation * Vector3.down, (head.position - frame.palm).normalized) > 0.65f;
            // OpenXR skeleton on Quest can leave Pinches/PinchStrength empty. Use measured tip distance.
            pinching[hand] = Vector3.Distance(frame.indexTip, Joint(state, 5)) < (pinching[hand] ? 0.026f : 0.016f) * scale;
            int curled = 0;
            for (int finger = 0; finger < 4; finger++)
                if (Vector3.Distance(Joint(state, 10 + 5 * finger), frame.palm) < (gripping[hand] ? 0.095f : 0.07f) * scale) curled++;
            gripping[hand] = curled >= 3 && Vector3.Distance(frame.indexTip, frame.palm) < (gripping[hand] ? 0.08f : 0.06f) * scale;
            frame.pinch = pinching[hand] && !frame.systemGesture;
            frame.grip = gripping[hand] && !frame.systemGesture;
            if ((state.Status & OVRPlugin.HandStatus.InputStateValid) != 0)
            {
                var pointer = state.PointerPose.ToOVRPose();
                frame.ray = new Ray(space.TransformPoint(pointer.position), space.rotation * pointer.orientation * Vector3.forward);
            }
            else
            {
                var shoulder = head.TransformPoint(new Vector3(hand == 0 ? -0.16f : 0.16f, -0.2f, -0.05f));
                frame.ray = new Ray(frame.pinchPoint, (frame.pinchPoint - shoulder).normalized);
            }
            return frame;
        }
    }

    public static class ScreenGeometry
    {
        // Opposite corner remains fixed; diagonal projection preserves the video aspect ratio.
        public static float CornerWidth(Vector2 anchorToCorner, Vector2 sign, float aspect, float minimum, float maximum)
        {
            var diagonal = new Vector2(sign.x, sign.y / aspect);
            return Mathf.Clamp(Vector2.Dot(anchorToCorner, diagonal) / diagonal.sqrMagnitude, minimum, maximum);
        }
        public static bool Intersect(Ray ray, Vector3 position, Vector3 normal, out Vector3 point)
        {
            point = Vector3.zero;
            if (!new Plane(normal, position).Raycast(ray, out float distance) || distance > 10) return false;
            point = ray.GetPoint(distance); return true;
        }
    }
}
