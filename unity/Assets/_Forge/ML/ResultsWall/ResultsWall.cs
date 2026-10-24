using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.Rendering;
using UnityEngine.UI;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace Forge.ML
{
    [RequireComponent(typeof(ForgePanel))]
    public class ResultsWall : MonoBehaviour
    {
        public MLSession session;
        const int MaxSamples = 1023;
        const int DotColumns = 32;
        const int GalleryPageSize = 24;
        const int GalleryColumns = 4;
        const float DotSize = 8.3f;
        const float DotColumnSpacing = 9.4f;
        const float DotRowSpacing = 8.9f;
        readonly Matrix4x4[] dotMatrices = new Matrix4x4[MaxSamples];
        readonly Vector4[] dotUvs = new Vector4[MaxSamples];
        readonly Vector4[] dotColors = new Vector4[MaxSamples];
        readonly int[] visibleSamples = new int[MaxSamples];
        RectTransform root, content, dynamicContent, detail;
        RectTransform hoverTip;
        RawImage hoverImage;
        TMPro.TMP_Text hoverText;
        int classFilter = -1, truth = -1, predicted = -1, selected = -1, page;
        bool wrongDots, gallery, confidentMistakes, classPicker;
        int dotCount;
        ConfidenceComparer confidenceComparer;
        Material dotMaterial;
        MaterialPropertyBlock dotProperties;
        Mesh quad;
        TMPro.TMP_Text filterLabel;
        public int VisibleDotCount => gallery || classPicker ? 0 : dotCount;
        public bool IsGallery => gallery;
        public bool IsClassPicker => classPicker;
        public int ActiveClassFilter => classFilter;
        public int GalleryItemCount => gallery ? dotCount : 0;
        public int GalleryPage => page;
        public string SelectedId => selected >= 0 && selected < session.Data.samples.Length ? session.Data.samples[selected].id : null;

        void Start()
        {
            session = MLSession.Resolve(session); root = GetComponent<ForgePanel>().Content;
            confidenceComparer = new ConfidenceComparer(this);
            MLUI.Button(root, "All dots", 8, 4, 64, 28, ShowAll);
            MLUI.Button(root, "Wrong", 76, 4, 66, 28, ShowWrongDots);
            MLUI.Button(root, "Show mistakes", 146, 4, 104, 28, ShowMistakes);
            MLUI.Button(root, "Most confident", 254, 4, 112, 28, ShowConfidentMistakes);
            MLUI.Button(root, "By class", 370, 4, 75, 28, ShowClassPicker);
            MLUI.Button(root, "‹", 515, 4, 34, 28, () => ChangePage(-1));
            MLUI.Button(root, "›", 552, 4, 34, 28, () => ChangePage(1));
            content = MLUI.Rect("Dot wall", root, 8, 64, 320, 290);
            var surface = content.gameObject.AddComponent<Image>(); surface.color = MLUI.Ink;
            var input = content.gameObject.AddComponent<MLResultsWallInput>(); input.owner = this;
            dynamicContent = MLUI.Rect("Dynamic content", content, 0, 0, 320, 290);
            hoverTip = MLUI.Rect("Dot preview", content, 0, 0, 92, 78);
            MLUI.Fill(hoverTip, 0, 0, 92, 78, MLUI.Ink);
            hoverImage = MLUI.Rect("Thumbnail", hoverTip, 3, 3, 86, 52).gameObject.AddComponent<RawImage>(); hoverImage.raycastTarget = false;
            hoverText = MLUI.Text(hoverTip, "", 3, 57, 86, 18, 9);
            hoverTip.gameObject.SetActive(false);
            detail = MLUI.Rect("Detail", root, 338, 40, 250, 325);
            var materialSource = Resources.Load<Material>("MLResultsDotMaterial");
            dotMaterial = materialSource != null ? new Material(materialSource) : new Material(Shader.Find("Forge/ML/InstancedAtlas"));
            dotMaterial.enableInstancing = true; dotMaterial.mainTexture = session.Atlas;
            dotProperties = new MaterialPropertyBlock();
            var temp = GameObject.CreatePrimitive(PrimitiveType.Quad);
            quad = temp.GetComponent<MeshFilter>().sharedMesh; Destroy(temp);
            session.SamplesChanged += Refresh; session.PairSelected += FilterPair; session.Reloaded += OnReloaded; Refresh();
        }
        void OnReloaded() { dotMaterial.mainTexture = session.Atlas; if (selected >= session.Data.samples.Length) selected = -1; }

        public void FilterPair(int trueClass, int predictedClass)
        {
            truth = trueClass; predicted = predictedClass; wrongDots = gallery = confidentMistakes = classPicker = false;
            classFilter = -1; selected = -1; page = 0; Refresh();
        }

        void ShowAll() { wrongDots = gallery = confidentMistakes = classPicker = false; classFilter = truth = predicted = -1; selected = -1; page = 0; Refresh(); }
        void ShowWrongDots() { wrongDots = true; gallery = confidentMistakes = classPicker = false; classFilter = truth = predicted = -1; selected = -1; page = 0; Refresh(); }
        void ShowMistakes() { gallery = true; wrongDots = true; confidentMistakes = classPicker = false; classFilter = truth = predicted = -1; selected = -1; page = 0; Refresh(); }
        void ShowConfidentMistakes() { gallery = true; wrongDots = true; confidentMistakes = true; classPicker = false; classFilter = truth = predicted = -1; selected = -1; page = 0; Refresh(); }
        void ShowClassPicker() { classPicker = true; gallery = false; selected = -1; page = 0; Refresh(); }
        void ChooseClass(int index) { classFilter = index; gallery = true; wrongDots = true; confidentMistakes = false; classPicker = false; truth = predicted = -1; selected = -1; page = 0; Refresh(); }
        void ChangePage(int delta)
        {
            if (!gallery) return;
            int count = CountGallerySamples();
            int lastPage = Mathf.Max(0, (count - 1) / GalleryPageSize);
            page = Mathf.Clamp(page + delta, 0, lastPage); Refresh();
        }

        void Refresh()
        {
            if (content == null) return;
            MLUI.Clear(dynamicContent);
            if (filterLabel == null) filterLabel = MLUI.Text(root, "", 8, 36, 490, 24, 13);
            filterLabel.text = classPicker ? "Choose a class to browse its mistakes" :
                (truth >= 0 ? $"True {session.Data.classes[truth]} → {session.Data.classes[predicted]}" :
                (gallery ? (confidentMistakes ? "Most confident mistakes" : "Mistakes gallery") : (wrongDots ? "Wrong predictions" : "All validation images")) +
                (classFilter < 0 ? "" : " · " + session.Data.classes[classFilter]));
            dotCount = 0;
            if (classPicker) { DrawClassPicker(); ShowDetail(); return; }
            BuildVisibleSamples();
            if (gallery) DrawGallery(); else DrawDots();
            ShowDetail();
        }

        void BuildVisibleSamples()
        {
            for (int i = 0; i < session.Data.samples.Length && dotCount < MaxSamples; i++)
            {
                var s = session.Data.samples[i];
                if (s.removed || ((wrongDots || gallery) && (s.correct || s.pred_idx < 0)) || (classFilter >= 0 && s.true_idx != classFilter) ||
                    (truth >= 0 && (s.true_idx != truth || s.pred_idx != predicted))) continue;
                visibleSamples[dotCount++] = i;
            }
            if (confidentMistakes) Array.Sort(visibleSamples, 0, dotCount, confidenceComparer);
            else Array.Sort(visibleSamples, 0, dotCount, new TruthComparer(this));
        }

        void DrawDots()
        {
            for (int visual = 0; visual < dotCount; visual++)
            {
                int i = visibleSamples[visual]; var s = session.Data.samples[i];
                int column = visual % DotColumns, row = visual / DotColumns;
                var p = new Vector3(content.rect.xMin + 8 + column * DotColumnSpacing, content.rect.yMax - 8 - row * DotRowSpacing, -4);
                dotMatrices[visual] = content.localToWorldMatrix * Matrix4x4.TRS(p, Quaternion.identity, new Vector3(DotSize, DotSize, 1));
                Rect uv = session.UV(i); dotUvs[visual] = new Vector4(uv.x, uv.y, uv.width, uv.height);
                // Grey until a run has predicted it.
                dotColors[visual] = s.pred_idx < 0 ? new Color(.55f, .58f, .62f) : s.correct ? new Color(.45f, 1f, .58f) : new Color(1f, .32f, .35f);
            }
            if (dotCount == 0) MLUI.Text(dynamicContent, "No matching samples", 8, 10, 280, 30);
        }

        void DrawGallery()
        {
            page = Mathf.Clamp(page, 0, Mathf.Max(0, (dotCount - 1) / GalleryPageSize));
            int start = page * GalleryPageSize, end = Mathf.Min(dotCount, start + GalleryPageSize);
            for (int visual = start; visual < end; visual++)
            {
                int index = visibleSamples[visual], local = visual - start;
                int column = local % GalleryColumns, row = local / GalleryColumns;
                float x = column * 79f + 2, y = row * 44f + 4;
                var card = MLUI.Rect("Mistake " + session.Data.samples[index].id, dynamicContent, x, y, 76, 40);
                var image = card.gameObject.AddComponent<RawImage>(); image.texture = session.Atlas; image.uvRect = session.UV(index);
                image.color = new Color(1, .48f, .48f); image.raycastTarget = true;
                var button = card.gameObject.AddComponent<Button>(); button.targetGraphic = image;
                int picked = index; button.onClick.AddListener(() => SelectSample(picked));
                MLUI.Text(card, session.Data.samples[index].id, 1, 31, 74, 11, 9);
            }
            int pages = Mathf.Max(1, Mathf.CeilToInt(dotCount / (float)GalleryPageSize));
            MLUI.Text(dynamicContent, $"Page {page + 1}/{pages} · {dotCount} mistakes", 4, 270, 312, 12, 10);
            if (dotCount == 0) MLUI.Text(dynamicContent, "No mistakes in this class", 8, 10, 280, 30);
        }

        void DrawClassPicker()
        {
            for (int c = 0; c < session.Data.classes.Length; c++)
            {
                int label = c;
                MLUI.Button(dynamicContent, session.Data.classes[c], c % 2 * 154 + 6, c / 2 * 48 + 8, 144, 38, () => ChooseClass(label));
            }
            MLUI.Button(dynamicContent, "Cancel", 6, 254, 144, 30, ShowAll);
        }

        int CountGallerySamples()
        {
            int count = 0;
            foreach (var sample in session.Data.samples)
                if (!sample.removed && !sample.correct && sample.pred_idx >= 0 && (classFilter < 0 || sample.true_idx == classFilter)) count++;
            return count;
        }

        sealed class ConfidenceComparer : IComparer<int>
        {
            readonly ResultsWall owner;
            public ConfidenceComparer(ResultsWall owner) { this.owner = owner; }
            public int Compare(int a, int b)
            {
                var x = owner.session.Data.samples[a]; var y = owner.session.Data.samples[b];
                float cx = x.probs != null && x.pred_idx < x.probs.Length ? x.probs[x.pred_idx] : 0;
                float cy = y.probs != null && y.pred_idx < y.probs.Length ? y.probs[y.pred_idx] : 0;
                return cy.CompareTo(cx);
            }
        }
        sealed class TruthComparer : IComparer<int>
        {
            readonly ResultsWall owner;
            public TruthComparer(ResultsWall owner) { this.owner = owner; }
            public int Compare(int a, int b)
            {
                int order = owner.session.Data.samples[a].true_idx.CompareTo(owner.session.Data.samples[b].true_idx);
                return order != 0 ? order : a.CompareTo(b);
            }
        }

        void ShowDetail()
        {
            MLUI.Clear(detail);
            if (selected < 0 || session.Data.samples[selected].removed)
            { selected = -1; MLUI.Text(detail, "Select a dot or mistake to inspect", 0, 5, 240, 48); return; }
            var s = session.Data.samples[selected];
            int pred = Mathf.Clamp(s.pred_idx, 0, session.Data.classes.Length - 1);
            float confidence = s.probs != null && pred < s.probs.Length ? s.probs[pred] : 0;
            MLUI.Photo(detail, session, selected, 0, 0, 80, 80);
            MLUI.Text(detail, $"{s.name ?? s.id}\nPred: {session.Data.classes[pred]} · {confidence:P1}\nTrue: {session.Data.classes[s.true_idx]}", 88, 0, 160, 85, 13);
            for (int c = 0; c < session.Data.classes.Length; c++)
            {
                float p = s.probs != null && c < s.probs.Length ? s.probs[c] : 0; float y = 88 + c * 17;
                MLUI.Text(detail, session.Data.classes[c], 0, y, 85, 17, 11);
                MLUI.Fill(detail, 88, y + 3, 110 * Mathf.Clamp01(p), 10, new Color(.25f, .65f, 1));
                MLUI.Text(detail, p.ToString("P0"), 202, y, 40, 17, 10);
            }
            MLUI.Button(detail, "Relabel", 0, 263, 76, 28, ClassPicker);
            MLUI.Button(detail, "Remove", 80, 263, 76, 28, () => session.Remove(s));
            MLUI.Button(detail, "Looks fine", 160, 263, 86, 28, DismissDetail);
        }

        public void SelectAt(PointerEventData eventData)
        {
            if (gallery || classPicker) return;
            if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(content, eventData.position, eventData.pressEventCamera, out Vector2 local)) return;
            int column = Mathf.FloorToInt((local.x - content.rect.xMin - 2f) / DotColumnSpacing);
            int row = Mathf.FloorToInt((content.rect.yMax - local.y - 2f) / DotRowSpacing);
            int dot = row * DotColumns + column;
            if (column < 0 || column >= DotColumns || dot < 0 || dot >= dotCount) return;
            SelectSample(visibleSamples[dot]);
        }
        public void HoverAt(PointerEventData eventData)
        {
            if (gallery || classPicker || dotCount == 0) { hoverTip.gameObject.SetActive(false); return; }
            if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(content, eventData.position, eventData.pressEventCamera, out Vector2 local)) return;
            int column = Mathf.FloorToInt((local.x - content.rect.xMin - 2f) / DotColumnSpacing);
            int row = Mathf.FloorToInt((content.rect.yMax - local.y - 2f) / DotRowSpacing);
            int dot = row * DotColumns + column;
            if (column < 0 || column >= DotColumns || dot < 0 || dot >= dotCount) { hoverTip.gameObject.SetActive(false); return; }
            int sampleIndex = visibleSamples[dot]; var sample = session.Data.samples[sampleIndex];
            hoverImage.texture = session.Atlas; hoverImage.uvRect = session.UV(sampleIndex);
            hoverText.text = $"{sample.name ?? sample.id} · {session.Data.classes[sample.true_idx]}";
            hoverTip.anchoredPosition = new Vector2(Mathf.Clamp(local.x - content.rect.xMin + 10, 0, content.rect.width - 92), -Mathf.Clamp(content.rect.yMax - local.y + 10, 0, content.rect.height - 78));
            hoverTip.SetAsLastSibling(); hoverTip.gameObject.SetActive(true);
        }
        public void HideHover() { if (hoverTip != null) hoverTip.gameObject.SetActive(false); }
        void SelectSample(int index) { selected = index; ShowDetail(); }
        public void SelectById(string id)
        {
            for (int i = 0; i < session.Data.samples.Length; i++) if (session.Data.samples[i].id == id)
            { gallery = classPicker = false; selected = i; Refresh(); return; }
        }
        void DismissDetail() { selected = -1; ShowDetail(); }
        void ClassPicker()
        {
            var previous = selected;
            MLUI.Clear(detail); MLUI.Text(detail, "Choose corrected class", 0, 0, 240, 30);
            for (int c = 0; c < session.Data.classes.Length; c++)
            {
                int label = c;
                MLUI.Button(detail, session.Data.classes[c], c % 2 * 124, 36 + c / 2 * 38, 118, 32, () => { selected = previous; session.Relabel(session.Data.samples[previous], label); });
            }
            MLUI.Button(detail, "Cancel", 0, 240, 118, 30, ShowDetail);
        }

        void Update()
        {
#if ENABLE_INPUT_SYSTEM
            bool close = Keyboard.current != null && Keyboard.current.xKey.wasPressedThisFrame;
#else
            bool close = Input.GetKeyDown(KeyCode.X);
#endif
            if (UnityEngine.XR.XRSettings.isDeviceActive)
            {
                var device = UnityEngine.XR.InputDevices.GetDeviceAtXRNode(UnityEngine.XR.XRNode.RightHand);
                if (device.TryGetFeatureValue(UnityEngine.XR.CommonUsages.secondaryButton, out bool pressed) && pressed) close = true;
            }
            if (close && selected >= 0) DismissDetail();
        }

        void LateUpdate()
        {
            if (gallery || classPicker || dotCount == 0 || dotMaterial == null || !SystemInfo.supportsInstancing) return;
            // Unity may keep scene objects alive when Enter Play Mode skips a domain reload.
            if (dotProperties == null) dotProperties = new MaterialPropertyBlock();
            dotProperties.SetVectorArray("_UVRect", dotUvs);
            dotProperties.SetVectorArray("_Color", dotColors);
            Graphics.DrawMeshInstanced(quad, 0, dotMaterial, dotMatrices, dotCount, dotProperties, ShadowCastingMode.Off, false, gameObject.layer);
        }
        void OnDestroy()
        {
            if (session != null) { session.SamplesChanged -= Refresh; session.PairSelected -= FilterPair; }
            if (dotMaterial != null) Destroy(dotMaterial);
        }
    }
}
