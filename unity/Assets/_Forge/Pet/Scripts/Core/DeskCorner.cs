using UnityEngine;

namespace Forge.Pet
{
    /// <summary>
    /// Where desk props sit (WhistleButton on the near-right corner until the DeskDock shows, GatorHatchery on the near-left): the corner of a
    /// horizontal rectangle on the edge nearest the viewer, on their right or left, with a rotation tilted toward them.
    /// </summary>
    public static class DeskCorner
    {
        /// <summary>
        /// Corner point on the top face (no lift) and a rotation whose up tilts tiltDegrees toward the viewer and whose
        /// forward points outward. userLeft = the viewer's left corner instead of the right. False for a degenerate rect.
        /// </summary>
        public static bool TryPose(Vector3 top, Quaternion rot, Vector2 size, Vector3 viewer, float inset, bool userLeft,
                                   float tiltDegrees, out Vector3 point, out Quaternion r)
        {
            point = default; r = Quaternion.identity;
            float hx = size.x * 0.5f, hz = size.y * 0.5f;
            if (hx <= 0.01f || hz <= 0.01f) return false;
            Vector3 l = Quaternion.Inverse(rot) * (viewer - top);

            // Near edge = the side the viewer is beyond the most (relative to the half extents).
            Vector3 outward, along;
            float halfAlong, edgeDist;
            if (Mathf.Abs(l.z) / hz >= Mathf.Abs(l.x) / hx)
            { outward = new Vector3(0f, 0f, l.z >= 0f ? 1f : -1f); along = Vector3.right; halfAlong = hx; edgeDist = hz; }
            else
            { outward = new Vector3(l.x >= 0f ? 1f : -1f, 0f, 0f); along = Vector3.forward; halfAlong = hz; edgeDist = hx; }

            Vector3 outwardW = rot * outward;
            Vector3 rightW = Vector3.Cross(Vector3.up, -outwardW);           // the viewer's right while facing the table
            if (userLeft) rightW = -rightW;
            float side = Vector3.Dot(rot * along, rightW) >= 0f ? 1f : -1f;

            Vector3 local = along * (side * Mathf.Max(0f, halfAlong - inset)) + outward * Mathf.Max(0f, edgeDist - inset);
            point = top + rot * local;
            r = TiltToward(outwardW, tiltDegrees);
            return true;
        }

        /// <summary>A prop's rotation at a desk edge: up tilted tiltDegrees toward the viewer, forward pointing outward (horizontal).</summary>
        public static Quaternion TiltToward(Vector3 outward, float tiltDegrees)
        {
            Vector3 tiltAxis = Vector3.Cross(Vector3.up, outward);
            Vector3 up = Quaternion.AngleAxis(tiltDegrees, tiltAxis) * Vector3.up;
            return Quaternion.LookRotation(Vector3.ProjectOnPlane(outward, up), up);
        }

        /// <summary>p moved horizontally to stay at least inset (m) inside the rectangle, at its own height.</summary>
        public static Vector3 ClampInside(Vector3 top, Quaternion rot, Vector2 size, Vector3 p, float inset)
        {
            Vector3 l = Quaternion.Inverse(rot) * (p - top);
            float hx = Mathf.Max(0f, size.x * 0.5f - inset), hz = Mathf.Max(0f, size.y * 0.5f - inset);
            l.x = Mathf.Clamp(l.x, -hx, hx);
            l.z = Mathf.Clamp(l.z, -hz, hz);
            return top + rot * l;
        }

        /// <summary>Horizontal distance (m) from p to the rectangle (0 inside).</summary>
        public static float HorizontalDistance(Vector3 top, Quaternion rot, Vector2 size, Vector3 p)
        {
            Vector3 l = Quaternion.Inverse(rot) * (p - top);
            float dx = Mathf.Max(0f, Mathf.Abs(l.x) - size.x * 0.5f), dz = Mathf.Max(0f, Mathf.Abs(l.z) - size.y * 0.5f);
            return Mathf.Sqrt(dx * dx + dz * dz);
        }
    }
}
