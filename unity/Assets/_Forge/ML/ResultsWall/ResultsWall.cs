using UnityEngine;
using UnityEngine.UI;

namespace Forge.ML
{
    [RequireComponent(typeof(ForgePanel))]
    public class ResultsWall : MonoBehaviour
    {
        public MLSession session;
        RectTransform root, grid, detail;
        int classFilter = -1, truth = -1, predicted = -1, selected = -1;
        bool wrongOnly;
        void Start()
        {
            session = MLSession.Resolve(session); root = GetComponent<ForgePanel>().Content;
            MLUI.Button(root, "All", 8, 4, 60, 28, () => { wrongOnly = false; classFilter = truth = predicted = -1; Refresh(); });
            MLUI.Button(root, "Wrong", 72, 4, 70, 28, () => { wrongOnly = true; truth = predicted = -1; Refresh(); });
            MLUI.Button(root, "Class >", 146, 4, 90, 28, () => { classFilter = (classFilter + 2) % (session.Data.classes.Length + 1) - 1; truth = predicted = -1; Refresh(); });
            var viewport = MLUI.Rect("Viewport", root, 8, 64, 320, 290);
            viewport.gameObject.AddComponent<Image>().color = MLUI.Ink;
            viewport.gameObject.AddComponent<Mask>().showMaskGraphic = true;
            grid = MLUI.Rect("Grid", viewport, 0, 0, 315, 290);
            var scroll = viewport.gameObject.AddComponent<ScrollRect>(); scroll.viewport = viewport; scroll.content = grid; scroll.horizontal = false;
            detail = MLUI.Rect("Detail", root, 338, 40, 250, 325);
            session.SamplesChanged += Refresh; session.PairSelected += FilterPair; Refresh();
        }
        public void FilterPair(int trueClass, int predictedClass) { truth = trueClass; predicted = predictedClass; wrongOnly = false; classFilter = -1; Refresh(); }
        void Refresh()
        {
            if (grid == null) return;
            MLUI.Clear(grid); MLUI.Clear(detail);
            // Filter label is kept outside the clipped grid.
            if (filterLabel == null) filterLabel = MLUI.Text(root, "", 8, 36, 320, 24, 13);
            filterLabel.text = truth >= 0 ? $"True {session.Data.classes[truth]} → {session.Data.classes[predicted]}" : (wrongOnly ? "Wrong" : "All") + " / " + (classFilter < 0 ? "all classes" : session.Data.classes[classFilter]);
            int visible = 0;
            for (int i = 0; i < session.Data.samples.Length; i++)
            {
                var s = session.Data.samples[i];
                if (s.removed || (wrongOnly && s.correct) || (classFilter >= 0 && s.true_idx != classFilter) || (truth >= 0 && (s.true_idx != truth || s.pred_idx != predicted))) continue;
                int index = i; float x = visible % 5 * 62, y = visible / 5 * 70;
                var button = MLUI.Button(grid, "", x, y, 58, 66, () => { selected = index; ShowDetail(); });
                button.image.color = s.correct ? new Color(.12f, .6f, .35f) : new Color(.8f, .2f, .25f);
                var photo = MLUI.Photo(button.transform, session, i, 3, 3, 52, 45);
                photo.color = s.correct ? new Color(.65f, 1, .7f) : new Color(1, .6f, .6f);
                MLUI.Text(button.transform, session.Data.classes[s.true_idx], 2, 49, 56, 16, 9); visible++;
            }
            grid.sizeDelta = new Vector2(315, Mathf.Max(290, Mathf.CeilToInt(visible / 5f) * 70));
            if (visible == 0) MLUI.Text(grid, "No matching samples", 8, 10, 280, 30);
            ShowDetail();
        }
        TMPro.TMP_Text filterLabel;
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
        void ClassPicker()
        {
            MLUI.Clear(detail); MLUI.Text(detail, "Choose corrected class", 0, 0, 240, 30);
            for (int c = 0; c < session.Data.classes.Length; c++) { int label = c; MLUI.Button(detail, session.Data.classes[c], c % 2 * 124, 36 + c / 2 * 38, 118, 32, () => session.Relabel(session.Data.samples[selected], label)); }
            MLUI.Button(detail, "Cancel", 0, 240, 118, 30, ShowDetail);
        }
        void OnDestroy() { if (session != null) { session.SamplesChanged -= Refresh; session.PairSelected -= FilterPair; } }
    }
}
