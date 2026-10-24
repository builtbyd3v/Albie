using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace Forge.ML
{
    /// <summary>
    /// Validation results: every image as a dot (GPU instanced, one draw), laid out as one block per class with its
    /// accuracy, green when right and red when wrong. Hover a dot to preview it, select it to inspect it in the card on
    /// the right (probabilities, Relabel / Remove / Looks fine). With nothing selected the card shows overall and
    /// per-class accuracy. The toolbar switches the wall to wrong-only, or to a paged gallery of mistakes (all, most
    /// confident first, or one class).
    /// </summary>
    [RequireComponent(typeof(ForgePanel))]
    public class ResultsWall : MonoBehaviour
    {
        public MLSession session;
        public Material dotMaterialAsset;
        const int MaxSamples = 1023;
        const int GalleryPageSize = 24;
        const int GalleryColumns = 4;
        const float Toolbar = 28f, WallTop = 58f, CardTop = 36f, CardWidth = 250f, Gap = 10f, Pad = 10f, BlockLabel = 15f;
        static readonly Color Pending = new Color(.55f, .58f, .62f);
        static readonly Color Popup = new Color(0.11f, 0.12f, 0.15f, 0.97f);
        readonly Vector2[] dotCenters = new Vector2[MaxSamples];   // top-left mm in the wall
        readonly Vector4[] dotUvs = new Vector4[MaxSamples];
        readonly Vector4[] dotColors = new Vector4[MaxSamples];
        readonly int[] visibleSamples = new int[MaxSamples];
        RectTransform root, content, dynamicContent, card;
        RectTransform hoverTip, hoverRing, selectRing;
        RawImage hoverImage;
        TMP_Text hoverTitle, hoverText, filterLabel, pageLabel;
        ForgePill allPill, wrongPill, mistakesPill, confidentPill, classPill, prevPill, nextPill;
        int classFilter = -1, truth = -1, predicted = -1, selected = -1, page, hovered = -1;
        bool wrongDots, gallery, confidentMistakes, classPicker, relabeling;
        int dotCount;
        float dotPitch = 9f;
        ConfidenceComparer confidenceComparer;
        StatusComparer statusComparer;
        MLDotField dots;
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
            confidenceComparer = new ConfidenceComparer(this); statusComparer = new StatusComparer(this);
            var r = root.rect; float wallWidth = r.width - CardWidth - Gap, wallHeight = r.height - WallTop;

            // Toolbar: the wall's two views, then the three galleries; paging on the right.
            float x = 0;
            allPill = Segment("All images", ref x, 86, ShowAll);
            wrongPill = Segment("Wrong only", ref x, 90, ShowWrongDots);
            x += 12;
            mistakesPill = Segment("Show mistakes", ref x, 108, ShowMistakes);
            confidentPill = Segment("Most confident", ref x, 112, ShowConfidentMistakes);
            classPill = Segment("By class", ref x, 76, ShowClassPicker);
            nextPill = MLUI.Pill(root, "›", r.width - 30, 0, 30, Toolbar, () => ChangePage(1));
            prevPill = MLUI.Pill(root, "‹", r.width - 64, 0, 30, Toolbar, () => ChangePage(-1));
            pageLabel = MLUI.Text(root, "", r.width - 150, 7, 80, 16, 10.5f); pageLabel.color = MLUI.Sub; pageLabel.alignment = TextAlignmentOptions.Right;

            filterLabel = MLUI.Caps(root, "", 2, 39, wallWidth - 170, 14, 9.5f);
            float key = wallWidth - 160;
            Key(ref key, "Correct", MLUI.Good); Key(ref key, "Wrong", MLUI.Bad); Key(ref key, "Pending", Pending);

            content = MLUI.Rect("Dot wall", root, 0, WallTop, wallWidth, wallHeight);
            var well = content.gameObject.AddComponent<ForgeSurface>(); well.color = MLUI.Ink; well.Corner = 14; well.Outline = 0.12f;
            var input = content.gameObject.AddComponent<MLResultsWallInput>(); input.owner = this;
            dynamicContent = MLUI.Rect("Dynamic content", content, 0, 0, wallWidth, wallHeight);
            dots = MLUI.Rect("Dots", content, 0, 0, wallWidth, wallHeight).gameObject.AddComponent<MLDotField>();
            dots.raycastTarget = false; if (dotMaterialAsset != null) dots.material = dotMaterialAsset;
            selectRing = Ring("Selected ring", 0.95f);
            hoverRing = Ring("Hover ring", 0.7f);
            hoverTip = MLUI.Rect("Dot preview", content, 0, 0, 158, 66);
            MLUI.Surface(hoverTip, 0, 0, 158, 66, Popup, 10, 0.3f);
            hoverImage = MLUI.Rect("Thumbnail", hoverTip, 7, 7, 52, 52).gameObject.AddComponent<RawImage>(); hoverImage.raycastTarget = false;
            hoverTitle = MLUI.Text(hoverTip, "", 67, 8, 86, 16, 11); hoverTitle.fontStyle = FontStyles.Bold; hoverTitle.textWrappingMode = TextWrappingModes.NoWrap;
            hoverText = MLUI.Text(hoverTip, "", 67, 25, 88, 36, 9.5f); hoverText.color = MLUI.Sub;
            hoverTip.gameObject.SetActive(false);

            card = MLUI.Rect("Detail", root, wallWidth + Gap, CardTop, CardWidth, r.height - CardTop);

            session.SamplesChanged += Refresh; session.PairSelected += FilterPair; session.Reloaded += OnReloaded; Refresh();
        }
        void OnReloaded() { if (selected >= session.Data.samples.Length) selected = -1; Refresh(); }

        ForgePill Segment(string title, ref float x, float width, UnityEngine.Events.UnityAction click)
        {
            var pill = MLUI.Pill(root, title, x, 0, width, Toolbar, click); x += width + 4; return pill;
        }
        void Key(ref float x, string label, Color color)
        {
            MLUI.Dot(root, x, 42, 7, color);
            var text = MLUI.Text(root, label, x + 10, 38, 50, 14, 9.5f); text.color = MLUI.Sub;
            x += 16 + text.GetPreferredValues(label).x;
        }
        RectTransform Ring(string name, float alpha)
        {
            var ring = MLUI.Surface(content, 0, 0, 10, 10, new Color(1, 1, 1, 0), 3, alpha); ring.name = name;
            ring.rectTransform.pivot = new Vector2(0.5f, 0.5f); ring.gameObject.SetActive(false); return ring.rectTransform;
        }

        public void FilterPair(int trueClass, int predictedClass)
        {
            truth = trueClass; predicted = predictedClass; wrongDots = gallery = confidentMistakes = classPicker = relabeling = false;
            classFilter = -1; selected = -1; page = 0; Refresh();
        }

        void ShowAll() { wrongDots = gallery = confidentMistakes = classPicker = relabeling = false; classFilter = truth = predicted = -1; selected = -1; page = 0; Refresh(); }
        void ShowWrongDots() { wrongDots = true; gallery = confidentMistakes = classPicker = relabeling = false; classFilter = truth = predicted = -1; selected = -1; page = 0; Refresh(); }
        void ShowMistakes() { gallery = true; wrongDots = true; confidentMistakes = classPicker = relabeling = false; classFilter = truth = predicted = -1; selected = -1; page = 0; Refresh(); }
        void ShowConfidentMistakes() { gallery = true; wrongDots = true; confidentMistakes = true; classPicker = relabeling = false; classFilter = truth = predicted = -1; selected = -1; page = 0; Refresh(); }
        void ShowClassPicker() { classPicker = true; gallery = relabeling = false; selected = -1; page = 0; Refresh(); }
        void ChooseClass(int index) { classFilter = index; gallery = true; wrongDots = true; confidentMistakes = false; classPicker = relabeling = false; truth = predicted = -1; selected = -1; page = 0; Refresh(); }
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
            HideHover();
            dotCount = 0;
            if (classPicker) DrawClassPicker();
            else
            {
                BuildVisibleSamples();
                if (gallery) DrawGallery(); else DrawDots();
            }
            dots.Set(session.Atlas, gallery || classPicker ? 0 : dotCount, dotCenters, dotUvs, dotColors, dotPitch * 0.8f);
            UpdateToolbar();
            ShowDetail();
        }

        void UpdateToolbar()
        {
            bool wall = !gallery && !classPicker;
            allPill.On = wall && !wrongDots && truth < 0;
            wrongPill.On = wall && wrongDots;
            mistakesPill.On = gallery && !confidentMistakes && classFilter < 0;
            confidentPill.On = gallery && confidentMistakes;
            classPill.On = classPicker || (gallery && classFilter >= 0);
            prevPill.gameObject.SetActive(gallery); nextPill.gameObject.SetActive(gallery);
            int pages = Mathf.Max(1, Mathf.CeilToInt(dotCount / (float)GalleryPageSize));
            pageLabel.text = gallery ? $"{page + 1} / {pages}" : "";
            string what = classPicker ? "Choose a class to browse its mistakes" :
                truth >= 0 ? $"True {session.Data.classes[truth]}  →  predicted {session.Data.classes[predicted]}" :
                gallery ? (confidentMistakes ? "Most confident mistakes" : "Mistakes") + (classFilter < 0 ? "" : "  ·  " + session.Data.classes[classFilter]) :
                wrongDots ? "Wrong predictions" : "All validation images";
            filterLabel.text = (classPicker ? what : $"{what}  ·  {dotCount:N0}").ToUpperInvariant();
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
            else Array.Sort(visibleSamples, 0, dotCount, statusComparer);
        }

        // One block per class (sorted right, wrong, pending inside each), sized to fill the wall; one flat block for a
        // confusion pair or more than 20 classes.
        void DrawDots()
        {
            float width = content.rect.width, height = content.rect.height;
            if (dotCount == 0) { Empty("No matching images"); return; }
            int classes = session.Data.classes.Length;
            bool blocks = truth < 0 && classes > 1 && classes <= 20;
            int blockCount = blocks ? classes : 1;
            var perBlock = new int[blockCount];
            for (int v = 0; v < dotCount; v++) perBlock[blocks ? Mathf.Clamp(session.Data.samples[visibleSamples[v]].true_idx, 0, classes - 1) : 0]++;
            int most = 1; foreach (int n in perBlock) most = Mathf.Max(most, n);
            float label = blocks ? BlockLabel : 0f, gapX = 14f, gapY = 8f;
            int columns = Mathf.CeilToInt(Mathf.Sqrt(most * (blocks ? 1f : width / height)));
            int rows = Mathf.CeilToInt(most / (float)columns);
            int across = 1; dotPitch = 0f;
            for (int bc = 1; bc <= blockCount; bc++)
            {
                int br = Mathf.CeilToInt(blockCount / (float)bc);
                float p = Mathf.Min((width - 2 * Pad - (bc - 1) * gapX) / (bc * columns), (height - 2 * Pad - br * label - (br - 1) * gapY) / (br * rows));
                if (p > dotPitch) { dotPitch = p; across = bc; }
            }
            dotPitch = Mathf.Min(dotPitch, 16f);
            float blockWidth = columns * dotPitch, blockHeight = label + rows * dotPitch;
            int down = Mathf.CeilToInt(blockCount / (float)across);
            float left = (width - (across * blockWidth + (across - 1) * gapX)) * 0.5f;
            float top = Mathf.Max(Pad, (height - (down * blockHeight + (down - 1) * gapY)) * 0.5f);

            if (blocks)
            {
                var seen = new int[classes]; var right = new int[classes];
                foreach (var s in session.Data.samples)
                    if (!s.removed && s.pred_idx >= 0 && s.true_idx >= 0 && s.true_idx < classes) { seen[s.true_idx]++; if (s.correct) right[s.true_idx]++; }
                for (int c = 0; c < classes; c++)
                {
                    float bx = left + c % across * (blockWidth + gapX), by = top + c / across * (blockHeight + gapY);
                    var name = MLUI.Text(dynamicContent, session.Data.classes[c], bx, by, blockWidth * 0.68f, 13, 9); name.color = MLUI.Sub;
                    name.textWrappingMode = TextWrappingModes.NoWrap; name.overflowMode = TextOverflowModes.Ellipsis;
                    float accuracy = seen[c] == 0 ? -1f : right[c] / (float)seen[c];
                    var value = MLUI.Text(dynamicContent, accuracy < 0 ? "—" : accuracy.ToString("P0"), bx + blockWidth * 0.5f, by, blockWidth * 0.5f, 13, 9);
                    value.alignment = TextAlignmentOptions.TopRight; value.fontStyle = FontStyles.Bold; value.color = AccuracyColor(accuracy);
                }
            }

            var placed = new int[blockCount];
            for (int visual = 0; visual < dotCount; visual++)
            {
                int i = visibleSamples[visual]; var s = session.Data.samples[i];
                int b = blocks ? Mathf.Clamp(s.true_idx, 0, classes - 1) : 0, k = placed[b]++;
                float bx = left + b % across * (blockWidth + gapX), by = top + b / across * (blockHeight + gapY) + label;
                var centre = new Vector2(bx + (k % columns + 0.5f) * dotPitch, by + (k / columns + 0.5f) * dotPitch);
                dotCenters[visual] = centre;
                Rect uv = session.UV(i); dotUvs[visual] = new Vector4(uv.x, uv.y, uv.width, uv.height);
                dotColors[visual] = StatusColor(s);
            }
        }

        void DrawGallery()
        {
            page = Mathf.Clamp(page, 0, Mathf.Max(0, (dotCount - 1) / GalleryPageSize));
            int start = page * GalleryPageSize, end = Mathf.Min(dotCount, start + GalleryPageSize);
            int rows = GalleryPageSize / GalleryColumns;
            float gap = 6f, tileWidth = (content.rect.width - 2 * Pad - (GalleryColumns - 1) * gap) / GalleryColumns;
            float tileHeight = Mathf.Min(48f, (content.rect.height - 2 * Pad - (rows - 1) * gap) / rows);
            for (int visual = start; visual < end; visual++)
            {
                int index = visibleSamples[visual], local = visual - start;
                var s = session.Data.samples[index];
                float x = Pad + local % GalleryColumns * (tileWidth + gap), y = Pad + local / GalleryColumns * (tileHeight + gap);
                var tile = HoverButton("Mistake " + s.id, dynamicContent, x, y, tileWidth, tileHeight, 0.045f, 9);
                int picked = index; tile.onClick.AddListener(() => SelectSample(picked));
                float photo = tileHeight - 6;
                MLUI.Photo(tile.transform, session, index, 3, 3, photo, photo);
                int pred = Mathf.Clamp(s.pred_idx, 0, session.Data.classes.Length - 1);
                var truthText = MLUI.Text(tile.transform, session.Data.classes[s.true_idx], photo + 9, 4, tileWidth - photo - 12, 12, 8.5f); truthText.color = MLUI.Sub;
                var predText = MLUI.Text(tile.transform, "→ " + session.Data.classes[pred], photo + 9, 15, tileWidth - photo - 12, 14, 10);
                predText.color = MLUI.Bad; predText.fontStyle = FontStyles.Bold;
                foreach (var t in new[] { truthText, predText }) { t.textWrappingMode = TextWrappingModes.NoWrap; t.overflowMode = TextOverflowModes.Ellipsis; }
                var confidence = MLUI.Text(tile.transform, Confidence(s).ToString("P0") + " sure", photo + 9, 29, tileWidth - photo - 12, 12, 8.5f); confidence.color = MLUI.Faint;
            }
            if (dotCount == 0) Empty("No mistakes here");
        }

        void DrawClassPicker()
        {
            int classes = session.Data.classes.Length;
            var mistakes = new int[classes];
            foreach (var s in session.Data.samples) if (!s.removed && !s.correct && s.pred_idx >= 0 && s.true_idx >= 0 && s.true_idx < classes) mistakes[s.true_idx]++;
            int rows = Mathf.CeilToInt(classes / 2f);
            float width = (content.rect.width - 2 * Pad - 8) / 2, height = Mathf.Min(32f, (content.rect.height - 2 * Pad - 40 - (rows - 1) * 6) / rows);
            for (int c = 0; c < classes; c++)
            {
                int label = c;
                var pill = MLUI.Pill(dynamicContent, $"{session.Data.classes[c]}   <color=#FFFFFF70>{mistakes[c]}</color>", Pad + c % 2 * (width + 8), Pad + c / 2 * (height + 6), width, height, () => ChooseClass(label));
                pill.name = session.Data.classes[c];
            }
            MLUI.Pill(dynamicContent, "Cancel", Pad, Pad + rows * (height + 6) + 4, width, 28, ShowAll);
        }

        void Empty(string message)
        {
            var text = MLUI.Text(dynamicContent, message, 0, content.rect.height * 0.5f - 10, content.rect.width, 20, 12);
            text.alignment = TextAlignmentOptions.Center; text.color = MLUI.Faint;
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
            public int Compare(int a, int b) => Confidence(owner.session.Data.samples[b]).CompareTo(Confidence(owner.session.Data.samples[a]));
        }
        // By class, then right before wrong before not yet predicted, so each class block reads like a bar.
        sealed class StatusComparer : IComparer<int>
        {
            readonly ResultsWall owner;
            public StatusComparer(ResultsWall owner) { this.owner = owner; }
            public int Compare(int a, int b)
            {
                var x = owner.session.Data.samples[a]; var y = owner.session.Data.samples[b];
                int order = x.true_idx.CompareTo(y.true_idx);
                if (order == 0) order = Status(x).CompareTo(Status(y));
                return order != 0 ? order : a.CompareTo(b);
            }
            static int Status(MLSample s) => s.pred_idx < 0 ? 2 : s.correct ? 0 : 1;
        }
        static float Confidence(MLSample s) => s.probs != null && s.pred_idx >= 0 && s.pred_idx < s.probs.Length ? s.probs[s.pred_idx] : 0f;
        static Color StatusColor(MLSample s) => s.pred_idx < 0 ? Pending : s.correct ? MLUI.Good : MLUI.Bad;
        static Color AccuracyColor(float accuracy) => accuracy < 0 ? MLUI.Faint : accuracy >= 0.8f ? MLUI.Good : accuracy >= 0.6f ? MLUI.Warn : MLUI.Bad;

        // A button whose whole surface brightens while pointed at (tiles, rows).
        static Button HoverButton(string name, Transform parent, float x, float y, float w, float h, float rest, float corner)
        {
            var surface = MLUI.Surface(parent, x, y, w, h, Color.white, corner, 0.15f); surface.name = name; surface.raycastTarget = true;
            var button = surface.gameObject.AddComponent<Button>(); button.targetGraphic = surface;
            var colors = button.colors;
            colors.normalColor = colors.selectedColor = colors.disabledColor = new Color(1, 1, 1, rest);
            colors.highlightedColor = new Color(1, 1, 1, rest + 0.06f); colors.pressedColor = new Color(1, 1, 1, rest + 0.1f);
            colors.fadeDuration = 0.08f; button.colors = colors;
            button.navigation = new Navigation { mode = Navigation.Mode.None };
            return button;
        }

        // ---- Card ----

        void ShowDetail()
        {
            MLUI.Clear(card);
            float w = card.rect.width, h = card.rect.height;
            MLUI.Card(card, 0, 0, w, h, 16);
            if (selected >= 0 && (selected >= session.Data.samples.Length || session.Data.samples[selected].removed)) { selected = -1; relabeling = false; }
            if (selected < 0) DrawOverview(w, h);
            else if (relabeling) DrawRelabel(w, h);
            else DrawSample(w, h);
            PlaceSelectRing();
        }

        void DrawOverview(float w, float h)
        {
            int classes = session.Data.classes.Length, total = 0, correct = 0, wrong = 0, removed = 0, pending = 0;
            var seen = new int[classes]; var right = new int[classes];
            foreach (var s in session.Data.samples)
            {
                if (s.removed) { removed++; continue; }
                if (s.pred_idx < 0) { pending++; continue; }
                total++; if (s.true_idx >= 0 && s.true_idx < classes) seen[s.true_idx]++;
                if (s.correct) { correct++; if (s.true_idx >= 0 && s.true_idx < classes) right[s.true_idx]++; } else wrong++;
            }
            float accuracy = total == 0 ? -1f : correct / (float)total;
            MLUI.Caps(card, "Validation accuracy", 16, 16, w - 32);
            var big = MLUI.Text(card, accuracy < 0 ? "—" : accuracy.ToString("0.0%"), 15, 28, w - 30, 44, 36); big.fontStyle = FontStyles.Bold;
            var line = MLUI.Text(card, $"{correct:N0} of {total:N0} correct" + (pending > 0 ? $"  ·  {pending:N0} waiting" : ""), 16, 72, w - 32, 16, 10.5f); line.color = MLUI.Sub;
            MLUI.Stat(card, "Wrong", wrong.ToString("N0"), 16, 96, 70, MLUI.Bad).fontSize = 18;
            MLUI.Stat(card, "Removed", removed.ToString("N0"), 92, 96, 70, MLUI.TextColor).fontSize = 18;
            MLUI.Stat(card, "Classes", classes.ToString(), 168, 96, 70, MLUI.TextColor).fontSize = 18;
            MLUI.Fill(card, 16, 138, w - 32, 1, new Color(1, 1, 1, 0.07f));
            MLUI.Caps(card, "Per class", 16, 147, 80);
            var hint = MLUI.Text(card, "tap for its mistakes", 80, 146, w - 96, 14, 9); hint.color = MLUI.Faint; hint.alignment = TextAlignmentOptions.TopRight;
            float rowTop = 164, row = Mathf.Clamp((h - rowTop - 10) / Mathf.Max(1, classes), 11f, 18f);
            float barX = 86, barWidth = w - barX - 52;
            for (int c = 0; c < classes && rowTop + (c + 1) * row <= h - 4; c++)
            {
                int label = c; float y = rowTop + c * row, value = seen[c] == 0 ? -1f : right[c] / (float)seen[c];
                var rowButton = HoverButton("Class " + session.Data.classes[c], card, 8, y, w - 16, row, 0f, 6);
                rowButton.onClick.AddListener(() => ChooseClass(label));
                var name = MLUI.Text(card, session.Data.classes[c], 16, y + row * 0.5f - 7, 68, 14, 10); name.textWrappingMode = TextWrappingModes.NoWrap; name.overflowMode = TextOverflowModes.Ellipsis;
                MLUI.Surface(card, barX, y + row * 0.5f - 2.5f, barWidth, 5, new Color(1, 1, 1, 0.07f), 2.5f);
                if (value > 0) MLUI.Surface(card, barX, y + row * 0.5f - 2.5f, Mathf.Max(5, barWidth * value), 5, AccuracyColor(value), 2.5f);
                var pct = MLUI.Text(card, value < 0 ? "—" : value.ToString("P0"), w - 52, y + row * 0.5f - 7, 36, 14, 10); pct.alignment = TextAlignmentOptions.TopRight; pct.color = MLUI.Sub;
            }
        }

        void DrawSample(float w, float h)
        {
            var s = session.Data.samples[selected];
            int classes = session.Data.classes.Length, pred = Mathf.Clamp(s.pred_idx, 0, classes - 1);
            Color status = StatusColor(s);
            MLUI.Caps(card, "Image", 16, 16, 60);
            var id = MLUI.Text(card, s.name ?? s.id, 62, 14, w - 106, 14, 9.5f); id.color = MLUI.Faint; id.textWrappingMode = TextWrappingModes.NoWrap; id.overflowMode = TextOverflowModes.Ellipsis;
            MLUI.Pill(card, "×", w - 40, 10, 26, 22, DismissDetail).name = "Close";
            MLUI.Surface(card, 14, 36, 100, 100, new Color(status.r, status.g, status.b, 0.18f), 10, 0.7f);
            MLUI.Photo(card, session, selected, 18, 40, 92, 92);
            float x = 128;
            MLUI.Caps(card, "Label", x, 38, w - x - 14);
            var truthText = MLUI.Text(card, session.Data.classes[s.true_idx], x, 50, w - x - 14, 18, 14); truthText.fontStyle = FontStyles.Bold;
            MLUI.Caps(card, "Predicted", x, 74, w - x - 14);
            var predText = MLUI.Text(card, s.pred_idx < 0 ? "—" : session.Data.classes[pred], x, 86, w - x - 14, 18, 14); predText.fontStyle = FontStyles.Bold; predText.color = status;
            var sure = MLUI.Text(card, s.pred_idx < 0 ? "not predicted yet" : $"{Confidence(s):P0} confident", x, 106, w - x - 14, 14, 10); sure.color = MLUI.Sub;
            MLUI.Surface(card, x, 122, 62, 15, new Color(status.r, status.g, status.b, 0.16f), 7.5f);
            var chip = MLUI.Caps(card, s.pred_idx < 0 ? "Pending" : s.correct ? "Correct" : "Wrong", x, 124, 62, 12, 8); chip.alignment = TextAlignmentOptions.Top; chip.color = status;

            MLUI.Caps(card, "Class probabilities", 16, 150, w - 32);
            float rowTop = 166, row = Mathf.Min(13.5f, (h - rowTop - 46) / Mathf.Max(1, classes));
            float barX = 84, barWidth = w - barX - 50;
            for (int c = 0; c < classes; c++)
            {
                float p = s.probs != null && c < s.probs.Length ? s.probs[c] : 0, y = rowTop + c * row;
                bool isPred = c == s.pred_idx, isTrue = c == s.true_idx;
                var name = MLUI.Text(card, session.Data.classes[c], 16, y, 66, row, 9.5f);
                name.color = isPred ? status : isTrue ? MLUI.TextColor : MLUI.Sub; name.textWrappingMode = TextWrappingModes.NoWrap; name.overflowMode = TextOverflowModes.Ellipsis;
                if (isPred || isTrue) name.fontStyle = FontStyles.Bold;
                MLUI.Surface(card, barX, y + row * 0.5f - 2.5f, barWidth, 5, new Color(1, 1, 1, 0.06f), 2.5f);
                Color fill = isPred ? status : isTrue ? new Color(1, 1, 1, 0.8f) : new Color(MLUI.Accent.r, MLUI.Accent.g, MLUI.Accent.b, 0.6f);
                if (p * barWidth >= 1f) MLUI.Surface(card, barX, y + row * 0.5f - 2.5f, Mathf.Max(5, barWidth * Mathf.Clamp01(p)), 5, fill, 2.5f);
                var pct = MLUI.Text(card, p.ToString("P0"), w - 50, y, 36, row, 9.5f); pct.alignment = TextAlignmentOptions.TopRight; pct.color = isPred ? status : MLUI.Sub;
            }
            float actions = h - 38;
            MLUI.Button(card, "Relabel", 14, actions, 70, 26, () => { relabeling = true; ShowDetail(); });
            MLUI.Button(card, "Remove", 90, actions, 66, 26, () => session.Remove(s));
            MLUI.Button(card, "Looks fine", 162, actions, w - 176, 26, DismissDetail);
        }

        void DrawRelabel(float w, float h)
        {
            var s = session.Data.samples[selected];
            int classes = session.Data.classes.Length;
            MLUI.Caps(card, "Relabel as", 16, 16, w - 60);
            MLUI.Pill(card, "×", w - 40, 10, 26, 22, () => { relabeling = false; ShowDetail(); }).name = "Close";
            var now = MLUI.Text(card, $"Labelled <b>{session.Data.classes[s.true_idx]}</b>" + (s.pred_idx >= 0 ? $"  ·  model says <b>{session.Data.classes[Mathf.Clamp(s.pred_idx, 0, classes - 1)]}</b>" : ""), 16, 30, w - 32, 16, 10);
            now.color = MLUI.Sub;
            MLUI.Photo(card, session, selected, 16, 52, 56, 56);
            int rows = Mathf.CeilToInt(classes / 2f);
            float pillWidth = (w - 32 - 8) / 2, pillHeight = Mathf.Min(28f, (h - 124 - 46 - (rows - 1) * 6) / rows);
            for (int c = 0; c < classes; c++)
            {
                int label = c; int previous = selected;
                var pill = MLUI.Pill(card, session.Data.classes[c], 16 + c % 2 * (pillWidth + 8), 120 + c / 2 * (pillHeight + 6), pillWidth, pillHeight,
                    () => { selected = previous; relabeling = false; session.Relabel(session.Data.samples[previous], label); });
                pill.On = c == s.pred_idx;
            }
            MLUI.Button(card, "Cancel", 16, h - 38, w - 32, 26, () => { relabeling = false; ShowDetail(); });
        }

        // ---- Pointer on the wall ----

        public void SelectAt(PointerEventData eventData)
        {
            if (gallery || classPicker) return;
            int dot = DotAt(eventData);
            if (dot >= 0) SelectSample(visibleSamples[dot]);
        }
        public void HoverAt(PointerEventData eventData)
        {
            if (gallery || classPicker || dotCount == 0) { HideHover(); return; }
            int dot = DotAt(eventData);
            if (dot < 0) { HideHover(); return; }
            if (dot == hovered) return;
            hovered = dot;
            int sampleIndex = visibleSamples[dot]; var sample = session.Data.samples[sampleIndex];
            Vector2 centre = dotCenters[dot];
            hoverRing.anchoredPosition = new Vector2(centre.x, -centre.y); hoverRing.sizeDelta = Vector2.one * dotPitch * 1.15f;
            hoverRing.GetComponent<ForgeSurface>().color = new Color(1, 1, 1, 0); hoverRing.gameObject.SetActive(true);
            hoverImage.texture = session.Atlas; hoverImage.uvRect = session.UV(sampleIndex);
            hoverTitle.text = session.Data.classes[sample.true_idx];
            int pred = Mathf.Clamp(sample.pred_idx, 0, session.Data.classes.Length - 1);
            string hex = ColorUtility.ToHtmlStringRGB(StatusColor(sample));
            hoverText.text = sample.pred_idx < 0 ? $"{sample.name ?? sample.id}\nnot predicted yet"
                : $"<color=#{hex}>{(sample.correct ? "Correct" : "→ " + session.Data.classes[pred])}</color>  {Confidence(sample):P0}\n{sample.name ?? sample.id}";
            float x = centre.x + dotPitch, y = centre.y - 66 - dotPitch * 0.6f;
            if (x + 158 > content.rect.width - 4) x = centre.x - dotPitch - 158;
            if (y < 4) y = centre.y + dotPitch;
            hoverTip.anchoredPosition = new Vector2(x, -y);
            hoverTip.SetAsLastSibling(); hoverTip.gameObject.SetActive(true);
        }
        public void HideHover()
        {
            hovered = -1;
            if (hoverTip != null) hoverTip.gameObject.SetActive(false);
            if (hoverRing != null) hoverRing.gameObject.SetActive(false);
        }
        int DotAt(PointerEventData eventData)
        {
            var hit = eventData.pointerCurrentRaycast;
            if (hit.gameObject == null) return -1;
            Vector3 local = content.InverseTransformPoint(hit.worldPosition);
            var p = new Vector2(local.x - content.rect.xMin, content.rect.yMax - local.y);
            int best = -1; float bestDistance = dotPitch * dotPitch * 0.5f;
            for (int i = 0; i < dotCount; i++)
            {
                float d = (dotCenters[i] - p).sqrMagnitude;
                if (d < bestDistance) { bestDistance = d; best = i; }
            }
            return best;
        }
        void PlaceSelectRing()
        {
            int dot = -1;
            if (selected >= 0 && !gallery && !classPicker) for (int i = 0; i < dotCount; i++) if (visibleSamples[i] == selected) { dot = i; break; }
            selectRing.gameObject.SetActive(dot >= 0);
            if (dot < 0) return;
            selectRing.anchoredPosition = new Vector2(dotCenters[dot].x, -dotCenters[dot].y); selectRing.sizeDelta = Vector2.one * dotPitch * 1.3f;
        }
        void SelectSample(int index) { selected = index; relabeling = false; ShowDetail(); }
        public void SelectById(string id)
        {
            for (int i = 0; i < session.Data.samples.Length; i++) if (session.Data.samples[i].id == id)
            { gallery = classPicker = relabeling = false; selected = i; Refresh(); return; }
        }
        void DismissDetail() { selected = -1; relabeling = false; ShowDetail(); }

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

        void OnDestroy()
        {
            if (session != null) { session.SamplesChanged -= Refresh; session.PairSelected -= FilterPair; session.Reloaded -= OnReloaded; }
        }
    }
}
