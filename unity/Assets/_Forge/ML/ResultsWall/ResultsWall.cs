using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.Rendering;
using UnityEngine.UI;

namespace Forge.ML
{
    [RequireComponent(typeof(ForgePanel))]
    public class ResultsWall : MonoBehaviour
    {
        public MLSession session;
        const int MaxDots = 1023;
        // 32 × 32 holds all 1,000 validation samples inside the 320 × 290 wall.
        const int DotColumns = 32;
        const float DotSize = 8.3f;
        const float DotColumnSpacing = 9.4f;
        const float DotRowSpacing = 8.9f;
        readonly Matrix4x4[] dotMatrices = new Matrix4x4[MaxDots];
        readonly Vector4[] dotUvs = new Vector4[MaxDots];
        readonly Vector4[] dotColors = new Vector4[MaxDots];
        readonly int[] visibleSamples = new int[MaxDots];
        RectTransform root, dotWall, detail;
        int classFilter = -1, truth = -1, predicted = -1, selected = -1;
        bool wrongOnly, confidentMistakes;
        int dotCount;
        ConfidenceComparer confidenceComparer;
        Material dotMaterial;
        MaterialPropertyBlock dotProperties;
        Mesh quad;
        TMPro.TMP_Text noMatches;
        public int VisibleDotCount => dotCount;
        void Start()
        {
            session = MLSession.Resolve(session); root = GetComponent<ForgePanel>().Content;
            confidenceComparer = new ConfidenceComparer(this);
            MLUI.Button(root, "All", 8, 4, 60, 28, () => { wrongOnly = confidentMistakes = false; classFilter = truth = predicted = -1; Refresh(); });
            MLUI.Button(root, "Wrong", 72, 4, 70, 28, () => { wrongOnly = true; confidentMistakes = false; truth = predicted = -1; Refresh(); });
            MLUI.Button(root, "Mistakes", 146, 4, 84, 28, () => { wrongOnly = confidentMistakes = true; truth = predicted = -1; Refresh(); });
            MLUI.Button(root, "Class >", 234, 4, 90, 28, () => { classFilter = (classFilter + 2) % (session.Data.classes.Length + 1) - 1; confidentMistakes = false; truth = predicted = -1; Refresh(); });
            dotWall = MLUI.Rect("Dot wall", root, 8, 64, 320, 290);
            var surface = dotWall.gameObject.AddComponent<Image>();
            surface.color = MLUI.Ink;
            var input = dotWall.gameObject.AddComponent<MLResultsWallInput>(); input.owner = this;
            detail = MLUI.Rect("Detail", root, 338, 40, 250, 325);
            var materialSource = Resources.Load<Material>("MLResultsDotMaterial");
            dotMaterial = materialSource != null ? new Material(materialSource) : new Material(Shader.Find("Forge/ML/InstancedAtlas"));
            dotMaterial.enableInstancing = true; dotMaterial.mainTexture = session.Atlas;
            dotProperties = new MaterialPropertyBlock();
            var temp = GameObject.CreatePrimitive(PrimitiveType.Quad);
            quad = temp.GetComponent<MeshFilter>().sharedMesh;
            Destroy(temp);
            session.SamplesChanged += Refresh; session.PairSelected += FilterPair; Refresh();
        }
        public void FilterPair(int trueClass, int predictedClass) { truth = trueClass; predicted = predictedClass; wrongOnly = confidentMistakes = false; classFilter = -1; Refresh(); }
        void Refresh()
        {
            if (dotWall == null) return;
            MLUI.Clear(detail);
            if (filterLabel == null) filterLabel = MLUI.Text(root, "", 8, 36, 320, 24, 13);
            filterLabel.text = truth >= 0 ? $"True {session.Data.classes[truth]} → {session.Data.classes[predicted]}" : (confidentMistakes ? "Most confident mistakes" : (wrongOnly ? "Wrong" : "All")) + " / " + (classFilter < 0 ? "all classes" : session.Data.classes[classFilter]);
            dotCount = 0;
            for (int i = 0; i < session.Data.samples.Length; i++)
            {
                var s = session.Data.samples[i];
                if (s.removed || (wrongOnly && s.correct) || (classFilter >= 0 && s.true_idx != classFilter) || (truth >= 0 && (s.true_idx != truth || s.pred_idx != predicted))) continue;
                if (dotCount == MaxDots) break;
                visibleSamples[dotCount++] = i;
            }
            if (confidentMistakes) Array.Sort(visibleSamples, 0, dotCount, confidenceComparer);
            for (int visual = 0; visual < dotCount; visual++)
            {
                int i = visibleSamples[visual]; var s = session.Data.samples[i]; int column = visual % DotColumns, row = visual / DotColumns;
                var p = new Vector3(dotWall.rect.xMin + 8 + column * DotColumnSpacing, dotWall.rect.yMax - 8 - row * DotRowSpacing, -4);
                dotMatrices[visual] = dotWall.localToWorldMatrix * Matrix4x4.TRS(p, Quaternion.identity, new Vector3(DotSize, DotSize, 1));
                Rect uv = session.UV(i); dotUvs[visual] = new Vector4(uv.x, uv.y, uv.width, uv.height);
                Color tint = s.correct ? new Color(.45f, 1f, .58f) : new Color(1f, .32f, .35f);
                dotColors[visual] = tint;
            }
            if (noMatches == null) noMatches = MLUI.Text(dotWall, "No matching samples", 8, 10, 280, 30);
            noMatches.gameObject.SetActive(dotCount == 0);
            ShowDetail();
        }
        TMPro.TMP_Text filterLabel;
        sealed class ConfidenceComparer : IComparer<int>
        {
            readonly ResultsWall owner;
            public ConfidenceComparer(ResultsWall owner) { this.owner = owner; }
            public int Compare(int a, int b) => owner.session.Data.samples[b].probs[owner.session.Data.samples[b].pred_idx].CompareTo(owner.session.Data.samples[a].probs[owner.session.Data.samples[a].pred_idx]);
        }
        void ShowDetail()
        {
            MLUI.Clear(detail);
            if (selected < 0 || session.Data.samples[selected].removed) { MLUI.Text(detail, "Select an image to inspect", 0, 5, 240, 48); return; }
            var s = session.Data.samples[selected];
            MLUI.Photo(detail, session, selected, 0, 0, 80, 80);
            MLUI.Text(detail, $"{s.id}\nPred: {session.Data.classes[s.pred_idx]}\nTrue: {session.Data.classes[s.true_idx]}", 88, 0, 160, 85, 13);
            for (int c = 0; c < session.Data.classes.Length; c++)
            {
                float p = s.probs[c]; float y = 88 + c * 17;
                MLUI.Text(detail, session.Data.classes[c], 0, y, 85, 17, 11);
                MLUI.Fill(detail, 88, y + 3, 110 * p, 10, new Color(.25f, .65f, 1));
                MLUI.Text(detail, p.ToString("P0"), 202, y, 40, 17, 10);
            }
            MLUI.Button(detail, "Relabel", 0, 263, 76, 28, ClassPicker);
            MLUI.Button(detail, "Remove", 80, 263, 76, 28, () => session.Remove(s));
            MLUI.Button(detail, "Looks fine", 160, 263, 86, 28, () => { selected = -1; ShowDetail(); });
        }
        public void SelectAt(PointerEventData eventData)
        {
            if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(dotWall, eventData.position, eventData.pressEventCamera, out Vector2 local)) return;
            int column = Mathf.FloorToInt((local.x - dotWall.rect.xMin - 2f) / DotColumnSpacing);
            int row = Mathf.FloorToInt((dotWall.rect.yMax - local.y - 2f) / DotRowSpacing);
            int dot = row * DotColumns + column;
            if (column < 0 || column >= DotColumns || dot < 0 || dot >= dotCount) return;
            selected = visibleSamples[dot]; ShowDetail();
        }
        void LateUpdate()
        {
            if (dotCount == 0 || dotMaterial == null || !SystemInfo.supportsInstancing) return;
            dotProperties.SetVectorArray("_UVRect", dotUvs);
            dotProperties.SetVectorArray("_Color", dotColors);
            Graphics.DrawMeshInstanced(quad, 0, dotMaterial, dotMatrices, dotCount, dotProperties, ShadowCastingMode.Off, false, gameObject.layer);
        }
        void ClassPicker()
        {
            MLUI.Clear(detail); MLUI.Text(detail, "Choose corrected class", 0, 0, 240, 30);
            for (int c = 0; c < session.Data.classes.Length; c++) { int label = c; MLUI.Button(detail, session.Data.classes[c], c % 2 * 124, 36 + c / 2 * 38, 118, 32, () => session.Relabel(session.Data.samples[selected], label)); }
            MLUI.Button(detail, "Cancel", 0, 240, 118, 30, ShowDetail);
        }
        void OnDestroy()
        {
            if (session != null) { session.SamplesChanged -= Refresh; session.PairSelected -= FilterPair; }
            if (dotMaterial != null) Destroy(dotMaterial);
        }
    }
}
