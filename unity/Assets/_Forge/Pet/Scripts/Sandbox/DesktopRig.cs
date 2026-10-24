using UnityEngine;
using UnityEngine.InputSystem;

namespace Forge.Pet
{
    /// <summary>
    /// First-person desktop rig for the pet sandbox (Input System only).
    /// WASD/arrows move (Shift = faster), mouse looks while the cursor is locked or the right
    /// mouse button is held. Left click locks the cursor, Esc unlocks.
    /// </summary>
    [RequireComponent(typeof(CharacterController))]
    [DisallowMultipleComponent]
    public class DesktopRig : MonoBehaviour
    {
        [SerializeField, Tooltip("Camera height above the rig origin (m).")]
        float eyeHeight = 1.6f;
        [SerializeField, Tooltip("Walk speed (m/s).")]
        float moveSpeed = 2f;
        [SerializeField, Tooltip("Speed multiplier while Shift is held.")]
        float sprintMultiplier = 2f;
        [SerializeField, Tooltip("Degrees per pixel of mouse movement.")]
        float lookSensitivity = 0.1f;
        [SerializeField, Tooltip("Max look up/down angle (deg).")]
        float pitchLimit = 85f;
        [SerializeField, Tooltip("Gravity (m/s^2).")]
        float gravity = -9.81f;

        CharacterController cc;
        Camera cam;
        float yaw, pitch, verticalVelocity;

        void Awake()
        {
            cc = GetComponent<CharacterController>();
            cc.height = 1.8f;
            cc.radius = 0.25f;
            cc.center = new Vector3(0f, 0.9f, 0f);

            cam = GetComponentInChildren<Camera>(true);
            if (cam == null)
            {
                var go = new GameObject("Head");
                go.transform.SetParent(transform, false);
                cam = go.AddComponent<Camera>();
                go.AddComponent<AudioListener>();
                cam.nearClipPlane = 0.05f;
            }
            cam.tag = "MainCamera";
            cam.transform.localPosition = new Vector3(0f, eyeHeight, 0f);
            cam.transform.localRotation = Quaternion.identity;
            yaw = transform.eulerAngles.y;
        }

        void OnDisable()
        {
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
        }

        // Spawn pose, captured on the first frame so a fall off the floor can be undone.
        Vector3 spawnPos;
        Quaternion spawnRot;
        bool spawnCaptured;

        void Update()
        {
            if (!spawnCaptured) { spawnPos = transform.position; spawnRot = transform.rotation; spawnCaptured = true; }

            Mouse mouse = Mouse.current;
            Keyboard kb = Keyboard.current;
            bool locked = Cursor.lockState == CursorLockMode.Locked;

            // Cursor lock / unlock.
            if (!locked && mouse != null && mouse.leftButton.wasPressedThisFrame) SetLocked(true);
            if (locked && kb != null && kb.escapeKey.wasPressedThisFrame) SetLocked(false);

            // Look.
            if (mouse != null && (locked || mouse.rightButton.isPressed))
            {
                Vector2 d = mouse.delta.ReadValue() * lookSensitivity;
                yaw += d.x;
                pitch = Mathf.Clamp(pitch - d.y, -pitchLimit, pitchLimit);
            }
            transform.rotation = Quaternion.Euler(0f, yaw, 0f);
            cam.transform.localRotation = Quaternion.Euler(pitch, 0f, 0f);

            // Move.
            Vector2 input = Vector2.zero;
            float speed = moveSpeed;
            if (kb != null)
            {
                if (kb.wKey.isPressed || kb.upArrowKey.isPressed) input.y += 1f;
                if (kb.sKey.isPressed || kb.downArrowKey.isPressed) input.y -= 1f;
                if (kb.dKey.isPressed || kb.rightArrowKey.isPressed) input.x += 1f;
                if (kb.aKey.isPressed || kb.leftArrowKey.isPressed) input.x -= 1f;
                if (kb.leftShiftKey.isPressed) speed *= sprintMultiplier;
            }
            Vector3 move = transform.forward * input.y + transform.right * input.x;
            if (move.sqrMagnitude > 1f) move.Normalize();
            move *= speed;

            verticalVelocity = cc.isGrounded ? -1f : verticalVelocity + gravity * Time.deltaTime;
            move.y = verticalVelocity;
            cc.Move(move * Time.deltaTime);

            // Safety net: walked off the sandbox floor -> back to the spawn pose instead of falling forever.
            if (transform.position.y < -5f) Respawn();
        }

        void Respawn()
        {
            cc.enabled = false; // CharacterController ignores direct transform writes while enabled
            transform.SetPositionAndRotation(spawnPos, spawnRot);
            cc.enabled = true;
            verticalVelocity = 0f;
            yaw = spawnRot.eulerAngles.y;
            pitch = 0f;
        }

        static void SetLocked(bool locked)
        {
            Cursor.lockState = locked ? CursorLockMode.Locked : CursorLockMode.None;
            Cursor.visible = !locked;
        }
    }
}
