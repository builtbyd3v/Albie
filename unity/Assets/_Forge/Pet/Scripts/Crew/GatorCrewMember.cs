using UnityEngine;

namespace Forge.Pet
{
    /// <summary>
    /// Added by GatorCrew to every hatched gator: its type (size, hat colour/shape), the hatch / go-home animation
    /// (the model child grows from nothing, so the root keeps its scale and the caption stays the right size), a
    /// greeting, and a status line when poked. The hat (shown once hatched) is built from primitives under the gator root and follows the
    /// head bone every LateUpdate (position only, the gator's yaw), so it works whatever the bone axes are.
    /// </summary>
    [DisallowMultipleComponent]
    public class GatorCrewMember : MonoBehaviour
    {
        const float HatchSeconds = 0.45f, GoHomeSeconds = 0.3f;

        static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
        static readonly int ColorId = Shader.PropertyToID("_Color");

        public GatorAgentType Type { get; private set; }
        public string PetId => Type != null ? Type.Id : name;
        public GatorController Controller { get; private set; }
        public GatorAgentBridge Bridge { get; private set; }
        public bool Leaving { get; private set; }

        Material _hatMaterial;
        Transform _model, _head, _hat;
        Vector3 _modelScale = Vector3.one;
        float _animStart, _hatLift = -1f;
        bool _hatching;
        GatorCaption _caption;
        GatorInteraction _interaction;

        /// <summary>Called by GatorCrew on the inactive clone, before Awake runs on anything.</summary>
        public void Init(GatorAgentType type, Material hatMaterial)
        {
            Type = type;
            _hatMaterial = hatMaterial;
            transform.localScale = Vector3.one * type.Scale;
        }

        void Awake()
        {
            Controller = GetComponent<GatorController>();
            Bridge = GetComponent<GatorAgentBridge>();
            _caption = GetComponentInChildren<GatorCaption>(true);
            _interaction = GetComponent<GatorInteraction>();
            Animator animator = GetComponentInChildren<Animator>(true);
            _model = animator != null && animator.transform != transform ? animator.transform : null;
            if (_model != null) _modelScale = _model.localScale;
            _head = animator != null ? FindChild(animator.transform, "head") : null;
            BuildHat();
            var tag = gameObject.AddComponent<GatorNameTag>();
            if (Type != null) tag.Set(Type.Name, Type.Color);
            BeginHatch();
        }

        void Start()
        {
            if (_interaction != null) _interaction.Poked += OnPoked;
            if (_caption != null && Type != null) _caption.SetText("Hi! I'm " + Type.Name + ". I " + Type.Role + ".", false);
        }

        void OnDestroy()
        {
            if (_interaction != null) _interaction.Poked -= OnPoked;
        }

        /// <summary>Shrink back into nothing, then destroy the gator.</summary>
        public void GoHome()
        {
            if (Leaving) return;
            Leaving = true;
            _hatching = false;
            _animStart = Time.time;
            if (_caption != null) _caption.SetText("Bye!", false);
            if (_model == null) Destroy(gameObject);
        }

        void BeginHatch()
        {
            if (_model == null) return;
            _hatching = true;
            _animStart = Time.time;
            _model.localScale = _modelScale * 0.01f;
        }

        void Update()
        {
            if (_model == null) return;
            float t = Time.time - _animStart;
            if (Leaving)
            {
                float u = Mathf.Clamp01(t / GoHomeSeconds);
                _model.localScale = _modelScale * Mathf.Max(0.01f, 1f - u * u);
                if (_hat != null) _hat.localScale = Vector3.one * Mathf.Max(0.01f, 1f - u);
                if (u >= 1f) Destroy(gameObject);
            }
            else if (_hatching)
            {
                float u = Mathf.Clamp01(t / HatchSeconds);
                float back = 1f + 2.2f * Mathf.Pow(u - 1f, 3f) + 1.2f * Mathf.Pow(u - 1f, 2f);   // ease-out-back (overshoot)
                _model.localScale = _modelScale * Mathf.Max(0.01f, back);
                if (u >= 1f) { _hatching = false; _model.localScale = _modelScale; }
            }
        }

        void LateUpdate()
        {
            if (_hat == null) return;
            if (_hatLift < 0f)
            {
                MeasureHatLift();                     // once, at full size; the hat pops on after the hatch
                if (_hatLift < 0f) return;
                _hat.gameObject.SetActive(true);
            }
            Transform anchor = _head != null ? _head : transform;     // no head bone: sit on top of the body
            float s = transform.lossyScale.y;
            _hat.SetPositionAndRotation(anchor.position + Vector3.up * (_hatLift * s * HatchFactor()),
                                        Quaternion.Euler(0f, transform.eulerAngles.y, 0f));
        }

        // While the model grows / shrinks the head bone moves with it; scale the lift the same way.
        float HatchFactor() => _model != null && _modelScale.y > 0f ? _model.localScale.y / _modelScale.y : 1f;

        // Distance (in unscaled units) from the head bone (or the root) up to the top of the body, measured once at full size.
        void MeasureHatLift()
        {
            if (_hatching) return;
            Transform anchor = _head != null ? _head : transform;
            Renderer[] rs = GetComponentsInChildren<SkinnedMeshRenderer>();
            float top = anchor.position.y;
            for (int i = 0; i < rs.Length; i++) top = Mathf.Max(top, rs[i].bounds.max.y);
            float s = Mathf.Max(0.0001f, transform.lossyScale.y);
            _hatLift = Mathf.Clamp((top - anchor.position.y) / s, 0.005f, 0.25f) - 0.004f;
        }

        void OnPoked()
        {
            if (_caption == null || Type == null || Leaving) return;
            bool busy = Bridge != null && Bridge.TaskInProgress;
            _caption.SetText(busy ? Type.Name + ": working on it!" : Type.Name + " here! I " + Type.Role + ".", false);
        }

        // ---------------------------------------------------------------- hat

        void BuildHat()
        {
            if (Type == null) return;
            _hat = new GameObject("Hat (" + Type.Name + ")").transform;
            _hat.SetParent(transform, false);
            Color c = Type.Color, dark = Color.Lerp(c, Color.black, 0.35f), light = Color.Lerp(c, Color.white, 0.6f);
            switch (Type.Hat)
            {
                case HatStyle.HardHat:
                    Part(PrimitiveType.Sphere, new Vector3(0.042f, 0.024f, 0.046f), new Vector3(0f, 0.004f, 0f), c);
                    Part(PrimitiveType.Cylinder, new Vector3(0.052f, 0.0015f, 0.058f), new Vector3(0f, 0.002f, 0.002f), c);
                    Part(PrimitiveType.Cube, new Vector3(0.006f, 0.008f, 0.04f), new Vector3(0f, 0.014f, 0f), dark);
                    break;
                case HatStyle.Beanie:
                    Part(PrimitiveType.Sphere, new Vector3(0.04f, 0.028f, 0.042f), new Vector3(0f, 0.004f, 0f), c);
                    Part(PrimitiveType.Cylinder, new Vector3(0.042f, 0.003f, 0.044f), new Vector3(0f, 0.0f, 0f), dark);
                    Part(PrimitiveType.Sphere, Vector3.one * 0.012f, new Vector3(0f, 0.02f, 0f), light);
                    break;
                case HatStyle.Antenna:
                    Part(PrimitiveType.Cylinder, new Vector3(0.003f, 0.011f, 0.003f), new Vector3(0f, 0.011f, 0f), dark);
                    Part(PrimitiveType.Sphere, Vector3.one * 0.012f, new Vector3(0f, 0.025f, 0f), c);
                    break;
                case HatStyle.Visor:
                    Part(PrimitiveType.Cylinder, new Vector3(0.038f, 0.004f, 0.04f), new Vector3(0f, 0.002f, 0f), c);
                    Part(PrimitiveType.Cylinder, new Vector3(0.032f, 0.0015f, 0.026f), new Vector3(0f, 0.002f, 0.022f), dark);
                    break;
                case HatStyle.Bandana:
                    Part(PrimitiveType.Cylinder, new Vector3(0.044f, 0.004f, 0.046f), new Vector3(0f, -0.002f, 0f), c);
                    Part(PrimitiveType.Sphere, new Vector3(0.01f, 0.008f, 0.01f), new Vector3(0f, -0.002f, -0.024f), c);
                    Part(PrimitiveType.Cube, new Vector3(0.006f, 0.002f, 0.014f), new Vector3(0.004f, -0.006f, -0.03f), dark);
                    break;
                case HatStyle.TopHat:
                    Part(PrimitiveType.Cylinder, new Vector3(0.046f, 0.0015f, 0.046f), new Vector3(0f, 0.001f, 0f), c);
                    Part(PrimitiveType.Cylinder, new Vector3(0.028f, 0.014f, 0.028f), new Vector3(0f, 0.015f, 0f), c);
                    Part(PrimitiveType.Cylinder, new Vector3(0.029f, 0.002f, 0.029f), new Vector3(0f, 0.005f, 0f), light);
                    break;
                case HatStyle.CaptainCap:
                    Part(PrimitiveType.Cylinder, new Vector3(0.04f, 0.007f, 0.042f), new Vector3(0f, 0.007f, 0f), c);
                    Part(PrimitiveType.Cylinder, new Vector3(0.034f, 0.0015f, 0.022f), new Vector3(0f, 0.002f, 0.018f), Color.Lerp(c, Color.black, 0.8f));
                    Part(PrimitiveType.Cylinder, new Vector3(0.041f, 0.002f, 0.043f), new Vector3(0f, 0.002f, 0f), new Color(0.12f, 0.16f, 0.3f));
                    Part(PrimitiveType.Sphere, Vector3.one * 0.007f, new Vector3(0f, 0.008f, 0.021f), new Color(1f, 0.8f, 0.2f));
                    break;
                case HatStyle.Bow:
                    Part(PrimitiveType.Sphere, new Vector3(0.016f, 0.012f, 0.009f), new Vector3(-0.009f, 0.004f, 0f), c);
                    Part(PrimitiveType.Sphere, new Vector3(0.016f, 0.012f, 0.009f), new Vector3(0.009f, 0.004f, 0f), c);
                    Part(PrimitiveType.Sphere, Vector3.one * 0.007f, new Vector3(0f, 0.004f, 0f), dark);
                    break;
                case HatStyle.Helmet:
                    Part(PrimitiveType.Sphere, new Vector3(0.048f, 0.032f, 0.052f), new Vector3(0f, 0.0f, 0f), c);
                    Part(PrimitiveType.Cube, new Vector3(0.004f, 0.012f, 0.036f), new Vector3(0f, 0.016f, -0.002f), new Color(0.85f, 0.2f, 0.2f));
                    break;
            }
            _hat.gameObject.SetActive(false);
        }

        void Part(PrimitiveType shape, Vector3 size, Vector3 localPos, Color color)
        {
            GameObject go = GameObject.CreatePrimitive(shape);
            Collider col = go.GetComponent<Collider>();
            if (col != null) Destroy(col);
            go.name = shape.ToString();
            Transform t = go.transform;
            t.SetParent(_hat, false);
            // Cylinders are 2 units tall: size.y is the half height, like the whistle's discs.
            t.localScale = size;
            t.localPosition = localPos;
            var r = go.GetComponent<Renderer>();
            if (_hatMaterial != null) r.sharedMaterial = _hatMaterial;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            var mpb = new MaterialPropertyBlock();
            mpb.SetColor(BaseColorId, color);
            mpb.SetColor(ColorId, color);
            r.SetPropertyBlock(mpb);
        }

        static Transform FindChild(Transform root, string childName)
        {
            if (root.name == childName) return root;
            for (int i = 0; i < root.childCount; i++)
            {
                Transform hit = FindChild(root.GetChild(i), childName);
                if (hit != null) return hit;
            }
            return null;
        }
    }
}
