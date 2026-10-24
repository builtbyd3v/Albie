using System.Collections.Generic;
using Meta.XR.MRUtilityKit;
using UnityEngine;

/// <summary>
/// Keeps workspace items inside the room. MRUK knows the walls (a headset's room scan, the simulator's room, desktop
/// mode's copy of it). An item whose slot pokes through a wall comes closer along its line from the eyes, down to arm's
/// reach, and if that isn't enough swings toward the middle, until it clears the walls by a few centimetres, landing
/// clear of the items already placed if it can. Poses are in the desk frame, like <see cref="Workspace"/>'s layout.
/// Per layout: <see cref="Begin"/>, <see cref="Take"/> each item that already fits, then <see cref="Fit"/> the rest.
/// </summary>
public static class RoomFit
{
    const float Margin = 0.04f;        // clearance from a wall (m)
    const float MinRadius = 0.45f;     // closest an item comes to the eyes (m)
    const float RadiusStep = 0.02f;
    const float AngleStep = 3f;        // deg
    const float Gap = 1f;              // between items, seen from the eyes (deg)

    static MRUKRoom room;
    static Transform frame;
    static Vector3 eye;
    // What each placed item covers, seen from the eyes: yaw and pitch (deg).
    static readonly List<Rect> taken = new List<Rect>();
    static readonly Vector3[] probes = new Vector3[8];

    /// <summary>Start fitting a layout: finds the room the eyes are in. False if there's none to fit against.</summary>
    public static bool Begin(Transform deskFrame, Vector3 eyeInFrame)
    {
        frame = deskFrame;
        eye = eyeInFrame;
        taken.Clear();
        room = null;
        if (MRUK.Instance == null || frame == null) return false;
        Vector3 eyeWorld = frame.TransformPoint(eye);
        foreach (var r in MRUK.Instance.Rooms)
            if (r != null && r.IsPositionInRoom(eyeWorld, false))
            {
                room = r;
                break;
            }
        return room != null;
    }

    /// <summary>
    /// Whether an item pokes through a wall. A panel is a <paramref name="size"/> rectangle about its position; a
    /// volume (<paramref name="volume"/>) is a box standing on its position.
    /// </summary>
    public static bool InWall(Vector3 position, Quaternion rotation, Vector3 size, bool volume) =>
        room != null && !Clear(position, rotation, size, volume);

    /// <summary>Keep this item's place for the ones fitted after it.</summary>
    public static void Take(Vector3 position, Vector3 size, bool volume) => taken.Add(Cover(position, size, volume));

    /// <summary>
    /// Move an item out of the walls and take its new place. With <paramref name="faceEye"/> it keeps facing the eyes
    /// (a panel tilts to them, a volume only turns); otherwise it keeps its rotation. Returns true if it moved.
    /// </summary>
    public static bool Fit(ref Vector3 position, ref Quaternion rotation, Vector3 size, bool volume, bool faceEye)
    {
        if (room == null) return false;
        Vector3 offset = position - eye;
        float radius = new Vector2(offset.x, offset.z).magnitude;
        float angle = Mathf.Atan2(offset.x, offset.z) * Mathf.Rad2Deg;
        float r0 = Mathf.Min(radius, MinRadius);
        // Twice through the same candidates: first clear of the walls and of the other items, then clear of the walls.
        for (int pass = 0; pass < 2; pass++)
        {
            for (float r = radius - RadiusStep; r >= MinRadius; r -= RadiusStep)
                if (TryAt(angle, r, offset.y, size, volume, faceEye, pass == 0, ref position, ref rotation)) return true;
            for (float a = Mathf.Abs(angle) - AngleStep; a >= 0f; a -= AngleStep)
                if (TryAt(a * Mathf.Sign(angle), r0, offset.y, size, volume, faceEye, pass == 0, ref position, ref rotation)) return true;
        }
        return false;
    }

    static bool TryAt(float angle, float radius, float up, Vector3 size, bool volume, bool faceEye, bool spaced,
        ref Vector3 position, ref Quaternion rotation)
    {
        float a = angle * Mathf.Deg2Rad;
        Vector3 p = eye + new Vector3(Mathf.Sin(a) * radius, up, Mathf.Cos(a) * radius);
        Quaternion q = rotation;
        if (faceEye)
        {
            Vector3 look = p - eye;
            if (volume) look.y = 0f;
            q = Quaternion.LookRotation(look, Vector3.up);
        }
        if (!Clear(p, q, size, volume)) return false;
        Rect cover = Cover(p, size, volume);
        if (spaced)
            foreach (var other in taken)
                if (other.Overlaps(cover)) return false;
        position = p;
        rotation = q;
        taken.Add(cover);
        return true;
    }

    // Seen from the eyes: the item's yaw and pitch extent (deg), padded by the gap.
    static Rect Cover(Vector3 position, Vector3 size, bool volume)
    {
        Vector3 center = volume ? position + Vector3.up * (size.y * 0.5f) : position;
        Vector3 d = center - eye;
        float flat = Mathf.Max(new Vector2(d.x, d.z).magnitude, 0.05f);
        float width = volume ? Mathf.Max(size.x, size.z) : size.x;
        float yaw = Mathf.Atan2(d.x, d.z) * Mathf.Rad2Deg, pitch = Mathf.Atan2(d.y, flat) * Mathf.Rad2Deg;
        float halfYaw = Mathf.Atan2(width * 0.5f, flat) * Mathf.Rad2Deg + Gap;
        float halfPitch = Mathf.Atan2(size.y * 0.5f, flat) * Mathf.Rad2Deg + Gap;
        return new Rect(yaw - halfYaw, pitch - halfPitch, 2f * halfYaw, 2f * halfPitch);
    }

    // Every probe, pushed out by the margin, inside the room's outline (walls only, not height). A panel: its corners
    // and edge midpoints. A volume: the corners of its box, at its base and its top.
    static bool Clear(Vector3 position, Quaternion rotation, Vector3 size, bool volume)
    {
        Vector3 right = rotation * Vector3.right;
        if (volume)
        {
            Vector3 forward = rotation * Vector3.forward;
            float hx = size.x * 0.5f + Margin, hz = size.z * 0.5f + Margin;
            for (int i = 0; i < 8; i++)
                probes[i] = position + right * ((i & 1) == 0 ? -hx : hx) + forward * ((i & 2) == 0 ? -hz : hz)
                          + Vector3.up * ((i & 4) == 0 ? 0f : size.y);
        }
        else
        {
            Vector3 up = rotation * Vector3.up;
            float hx = size.x * 0.5f + Margin, hy = size.y * 0.5f + Margin;
            probes[0] = position + right * hx + up * hy;
            probes[1] = position - right * hx + up * hy;
            probes[2] = position + right * hx - up * hy;
            probes[3] = position - right * hx - up * hy;
            probes[4] = position + right * hx;
            probes[5] = position - right * hx;
            probes[6] = position + up * hy;
            probes[7] = position - up * hy;
        }
        foreach (var p in probes)
            if (!room.IsPositionInRoom(frame.TransformPoint(p), false)) return false;
        return true;
    }
}
