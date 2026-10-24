using Meta.XR.MRUtilityKit;
using UnityEngine;

/// <summary>
/// Keeps workspace panels inside the room. MRUK knows the walls (a headset's room scan, the simulator's room, desktop
/// mode's copy of it). A panel whose default spot pokes through a wall comes closer along its line from the eyes, down
/// to arm's reach, and if that isn't enough swings toward the middle, until it clears the walls by a few centimetres.
/// Poses are in the desk frame, like <see cref="Workspace"/>'s layout.
/// </summary>
public static class RoomFit
{
    const float Margin = 0.04f;        // clearance from a wall (m)
    const float MinRadius = 0.45f;     // closest a panel comes to the eyes (m)
    const float RadiusStep = 0.02f;
    const float AngleStep = 3f;        // deg

    static readonly Vector3[] probes = new Vector3[8];

    /// <summary>
    /// Move a panel (or floating volume) of <paramref name="size"/> (m) out of the walls if it's in one. With
    /// <paramref name="faceEye"/> it turns to keep facing <paramref name="eye"/> (curved layout); otherwise it keeps
    /// its rotation. Returns true if it moved.
    /// </summary>
    public static bool Fit(Transform frame, Vector3 eye, ref Vector3 position, ref Quaternion rotation, Vector2 size, bool faceEye)
    {
        var room = MRUK.Instance != null ? MRUK.Instance.GetCurrentRoom() : null;
        if (room == null || frame == null) return false;
        // Out of the scanned room (or no walls to speak of): nothing sensible to fit against.
        if (!room.IsPositionInRoom(frame.TransformPoint(eye), false)) return false;
        if (Clear(room, frame, position, rotation, size)) return false;

        Vector3 offset = position - eye;
        float radius = new Vector2(offset.x, offset.z).magnitude;
        float angle = Mathf.Atan2(offset.x, offset.z) * Mathf.Rad2Deg;
        for (float r = radius - RadiusStep; r >= MinRadius; r -= RadiusStep)
            if (TryAt(room, frame, eye, angle, r, offset.y, size, faceEye, ref position, ref rotation)) return true;
        float r0 = Mathf.Min(radius, MinRadius);
        for (float a = Mathf.Abs(angle) - AngleStep; a >= 0f; a -= AngleStep)
            if (TryAt(room, frame, eye, a * Mathf.Sign(angle), r0, offset.y, size, faceEye, ref position, ref rotation)) return true;
        return false;
    }

    static bool TryAt(MRUKRoom room, Transform frame, Vector3 eye, float angle, float radius, float up, Vector2 size, bool faceEye,
        ref Vector3 position, ref Quaternion rotation)
    {
        float a = angle * Mathf.Deg2Rad;
        Vector3 p = eye + new Vector3(Mathf.Sin(a) * radius, up, Mathf.Cos(a) * radius);
        Quaternion q = faceEye ? Quaternion.LookRotation(p - eye, Vector3.up) : rotation;
        if (!Clear(room, frame, p, q, size)) return false;
        position = p;
        rotation = q;
        return true;
    }

    // Corners and edge midpoints, pushed out by the margin, all inside the room's outline (walls only, not height).
    static bool Clear(MRUKRoom room, Transform frame, Vector3 position, Quaternion rotation, Vector2 size)
    {
        Vector3 right = rotation * Vector3.right, up = rotation * Vector3.up;
        float hx = size.x * 0.5f + Margin, hy = size.y * 0.5f + Margin;
        probes[0] = position + right * hx + up * hy;
        probes[1] = position - right * hx + up * hy;
        probes[2] = position + right * hx - up * hy;
        probes[3] = position - right * hx - up * hy;
        probes[4] = position + right * hx;
        probes[5] = position - right * hx;
        probes[6] = position + up * hy;
        probes[7] = position - up * hy;
        foreach (var p in probes)
            if (!room.IsPositionInRoom(frame.TransformPoint(p), false)) return false;
        return true;
    }
}
