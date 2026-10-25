using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.Rendering;
using UnityEngine.UI;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace Forge.ML
{
    /// <summary>
    /// The dataset as a cover-flow deck of image cards (GPU instanced): the focused card faces you, its neighbours
    /// turn inward behind it. Scroll, swipe, drag or thumbstick coast through it with momentum and settle on a card;
    /// the barcode under it is the whole dataset by class, press or drag it to jump. Click a card to pick it: its
    /// metadata card flies out beside the deck, and the card goes back by itself once you scroll away (Put back, X
    /// and right B still work).
    /// </summary>
    [RequireComponent(typeof(ForgePanel))]
    public class DataStack : MonoBehaviour, IScrollHandler, IBeginDragHandler, IDragHandler, IEndDragHandler, IPointerDownHandler, IPointerEnterHandler, IPointerExitHandler, IPointerClickHandler
    {
        public MLSession session;
        public Material atlasMaterial;
        const int Range = 9, MaxCards = 2 * Range + 1;
        // Deck (mm): focused and side card sizes, how far the focused card pops toward you, the first neighbour's
        // offset and the spacing after it, the base depth in front of the panel, how far each card recedes, the turn.
        const float FocusSize = 150f, SideSize = 128f, Pop = 40f, Neighbor = 70f, Spacing = 18f, Base = 30f, Recede = 2.5f, Turn = 55f;
        const float DeckTop = 46f, DeckHeight = 186f, BarY = 288f, BarHeight = 10f, PreviewWidth = 258f, PreviewHeight = 226f;
        // Motion: coasting friction (1/s), the speed (cards/s) below which it settles on a card, the settle spring,
        // drag distance per card (mm), the wheel's push per notch (cards/s).
        const float Friction = 3.2f, SettleSpeed = 1.6f, SpringK = 60f, SpringDamp = 14f, MmPerCard = 34f, WheelPush = 4.2f;
        static readonly Color Popup = new Color(0.11f, 0.12f, 0.15f, 0.97f);

        readonly Matrix4x4[] matrices = new Matrix4x4[MaxCards];
        readonly Vector4[] uvs = new Vector4[MaxCards];
        readonly Vector4[] colors = new Vector4[MaxCards];
        // What was drawn this frame (far to near), for picking: sample index, centre x and half width (top-left mm), fade.
        readonly int[] drawn = new int[MaxCards];
        readonly float[] drawnX = new float[MaxCards], drawnHalf = new float[MaxCards], drawnAlpha = new float[MaxCards];
        int drawnCount;
        Material material;
        MaterialPropertyBlock properties;
        Mesh quad;
        RectTransform root, preview, playhead, focusTick, classTag;
        CanvasGroup previewFade, tagFade;
        RawImage image;
        TMP_Text header, cursor, label, prediction, metadata, classText;
        ForgeSurface classDot, imageRim;
        MLScrubBar bar;
        float position, velocity, slide, deckX, lastDragX, lastDragTime, dragVelocity, dragDistance, classTagWidth;
        int focus, selected = -1, goal = -1, pendingPick = -1;
        bool hovered, dragging, dragMoved, puttingBack;

        void Start()
        {
            session = MLSession.Resolve(session); root = GetComponent<ForgePanel>().Content;
            GetComponent<ForgePanel>().SizeMeters = new Vector2(.6f, .42f);
            // Transparent graphic ensures the panel receives scroll and drag from all uGUI input modules.
            var hit = root.gameObject.AddComponent<Image>(); hit.color = new Color(0,0,0,.001f);
            var relay = root.gameObject.AddComponent<MLStackInput>(); relay.owner = this;
            material = atlasMaterial != null ? new Material(atlasMaterial) : new Material(Shader.Find("Forge/ML/InstancedAtlas"));
            material.enableInstancing = true; material.mainTexture = session.Atlas;
            properties = new MaterialPropertyBlock();
            var temp = GameObject.CreatePrimitive(PrimitiveType.Quad); quad = temp.GetComponent<MeshFilter>().sharedMesh; Destroy(temp);
            float width = root.rect.width;
            deckX = width * 0.5f;

            header = MLUI.Caps(root, "", 4, 6, width * 0.6f, 14, 9.5f);
            var hint = MLUI.Text(root, "Scroll or swipe  ·  click a card to pick it", width * 0.4f, 4, width * 0.6f - 4, 14, 9.5f);
            hint.color = MLUI.Faint; hint.alignment = TextAlignmentOptions.TopRight; hint.textWrappingMode = TextWrappingModes.NoWrap;
            cursor = MLUI.Text(root, "", 4, 20, width - 8, 22, 14); cursor.textWrappingMode = TextWrappingModes.NoWrap;

            // The focused card's class, under it.
            classTag = MLUI.Rect("Class tag", root, 0, 0, 160, 16);
            tagFade = classTag.gameObject.AddComponent<CanvasGroup>(); tagFade.blocksRaycasts = false;
            classDot = MLUI.Dot(classTag, 0, 4, 8, Color.white);
            classText = MLUI.Text(classTag, "", 13, 0, 200, 16, 11.5f); classText.fontStyle = FontStyles.Bold; classText.textWrappingMode = TextWrappingModes.NoWrap;

            // The dataset barcode with the visible window and the focus on it; ‹ › step one card.
            float barX = 36, barWidth = width - 72;
            MLUI.Pill(root, "‹", 2, BarY - 6, 28, 22, () => Step(-1));
            MLUI.Pill(root, "›", width - 30, BarY - 6, 28, 22, () => Step(1));
            var barRect = MLUI.Rect("Dataset barcode", root, barX, BarY, barWidth, BarHeight);
            barRect.gameObject.AddComponent<CanvasRenderer>();
            bar = barRect.gameObject.AddComponent<MLScrubBar>(); bar.raycastTarget = true; bar.Bind(session);
            barRect.gameObject.AddComponent<MLScrubInput>().owner = this;
            playhead = MLUI.Surface(barRect, 0, -3, 10, BarHeight + 6, new Color(1, 1, 1, 0f), 3, 0.85f).rectTransform;
            focusTick = MLUI.Surface(barRect, 0, -5, 1.6f, BarHeight + 10, Color.white, 0.8f).rectTransform;
            var first = MLUI.Text(root, "1", barX, BarY + 13, 60, 12, 8.5f); first.color = MLUI.Faint;
            var last = MLUI.Text(root, session.Data.samples.Length.ToString("N0"), barX + barWidth - 60, BarY + 13, 60, 12, 8.5f);
            last.color = MLUI.Faint; last.alignment = TextAlignmentOptions.TopRight;

            // The picked image's card. Centre pivot so it can fly out of the deck and back.
            preview = MLUI.Rect("Picked image metadata", root, 0, 0, PreviewWidth, PreviewHeight);
            preview.pivot = new Vector2(0.5f, 0.5f);
            previewFade = preview.gameObject.AddComponent<CanvasGroup>();
            MLUI.Surface(preview, 0, 0, PreviewWidth, PreviewHeight, Popup, 16, 0.35f).raycastTarget = true;
            MLUI.Caps(preview, "Picked image", 14, 12, 160);
            imageRim = MLUI.Surface(preview, 12, 28, 92, 92, new Color(1, 1, 1, 0.12f), 10, 0.6f);
            image = MLUI.Photo(preview, session, 0, 16, 32, 84, 84);
            label = MLUI.Text(preview, "", 116, 28, PreviewWidth - 128, 34, 13); label.fontStyle = FontStyles.Bold;
            label.overflowMode = TextOverflowModes.Truncate;
            prediction = MLUI.Text(preview, "", 116, 66, PreviewWidth - 128, 50, 10.5f); prediction.lineSpacing = 10;
            metadata = MLUI.Text(preview, "", 14, 128, PreviewWidth - 28, 62, 9.5f); metadata.color = MLUI.Sub; metadata.lineSpacing = 6;
            metadata.overflowMode = TextOverflowModes.Ellipsis;
            MLUI.Pill(preview, "Put back", 14, PreviewHeight - 32, 84, 22, PutBack);
            var keep = MLUI.Text(preview, "or keep scrolling", 106, PreviewHeight - 28, 140, 14, 9.5f); keep.color = MLUI.Faint;
            preview.gameObject.SetActive(false);

            session.SamplesChanged += OnSamplesChanged; session.Reloaded += OnReloaded;
            SetHeader(); Refresh();
        }

        void OnSamplesChanged() { bar.SetVerticesDirty(); Refresh(); }
        void OnReloaded()
        {
            material.mainTexture = session.Atlas; image.texture = session.Atlas; bar.Bind(session);
            int last = Mathf.Max(0, session.Data.samples.Length - 1);
            position = Mathf.Clamp(position, 0, last); focus = Mathf.Clamp(focus, 0, last); goal = pendingPick = -1; velocity = 0;
            if (selected > last) { selected = -1; puttingBack = false; preview.gameObject.SetActive(false); }
            else if (selected >= 0) image.uvRect = session.UV(selected);
            SetHeader(); Refresh();
        }
        void SetHeader()
        {
            var samples = session.Data.samples;
            bool texts = samples.Length > 0 && !string.IsNullOrEmpty(samples[0].text);  // a text classifier: cards are its texts
            string size = !texts && samples.Length > 0 && samples[0].width > 0 ? $"  ·  {samples[0].width}×{samples[0].height}" : "";
            header.text = $"{samples.Length:N0} {(texts ? "texts" : "images")}  ·  {session.Data.classes.Length} classes{size}".ToUpperInvariant();
        }

        void Refresh()
        {
            if (cursor == null) return;
            var samples = session.Data.samples;
            if (samples.Length == 0) { cursor.text = "No images"; classText.text = ""; return; }
            focus = Mathf.Clamp(focus, 0, samples.Length - 1);
            var sample = samples[focus];
            cursor.text = $"<b>{focus + 1:N0}</b><color=#FFFFFF60> / {samples.Length:N0}</color>   <color=#FFFFFFA0>{sample.id}</color>" + (sample.removed ? "   <color=#FF5D6C>removed</color>" : "");
            int labelIndex = Mathf.Clamp(sample.label_idx, 0, session.Data.classes.Length - 1);
            classText.text = sample.removed ? "removed" : session.Data.classes[labelIndex];
            classDot.color = sample.removed ? MLUI.Bad : MLUI.ClassColor(labelIndex);
            classTagWidth = 13f + classText.GetPreferredValues(classText.text).x;
            if (selected >= 0 && selected < samples.Length)
            {
                var picked = samples[selected];
                int classes = session.Data.classes.Length;
                int pred = Mathf.Clamp(picked.pred_idx, 0, classes - 1), truth = Mathf.Clamp(picked.label_idx, 0, classes - 1);
                bool known = picked.probs != null && pred < picked.probs.Length;   // live epoch predictions come without probabilities
                float confidence = known ? picked.probs[pred] : 0;
                label.text = string.IsNullOrEmpty(picked.name) ? picked.id : picked.name;
                string tone = ColorUtility.ToHtmlStringRGB(picked.pred_idx < 0 ? MLUI.Faint : picked.correct ? MLUI.Good : MLUI.Bad);
                prediction.text = $"<color=#FFFFFF80>Label</color>   <b>{session.Data.classes[truth]}</b>\n" +
                    (picked.pred_idx < 0 ? "<color=#FFFFFF80>Model</color>   not predicted yet" : $"<color=#FFFFFF80>Model</color>   <b><color=#{tone}>{session.Data.classes[pred]}</color></b>  {(known ? confidence.ToString("0%") : "")}");
                imageRim.color = picked.pred_idx < 0 ? new Color(1, 1, 1, 0.12f) : picked.correct ? new Color(MLUI.Good.r, MLUI.Good.g, MLUI.Good.b, 0.3f) : new Color(MLUI.Bad.r, MLUI.Bad.g, MLUI.Bad.b, 0.3f);
                string path = string.IsNullOrEmpty(picked.path) ? "Not available" : picked.path;
                string bytes = picked.file_size > 0 ? $"{picked.file_size:N0} bytes" : "Not available";
                if (!string.IsNullOrEmpty(picked.text))  // the whole text: the card above only fits its first words
                    metadata.text = $"<color=#FFFFFFCC>{picked.text}</color>";
                else
                    metadata.text = $"Path: <color=#FFFFFFCC>{path}</color>\nSplit: <color=#FFFFFFCC>{picked.split ?? "val"}  ·  {picked.width} × {picked.height}  ·  {picked.channels} channels</color>\n" +
                        $"File size: <color=#FFFFFFCC>{bytes}</color>\nPrediction: <color=#FFFFFFCC>{session.Data.classes[pred]}{(known ? "  ·  " + confidence.ToString("0.0%") : "")}</color>";
            }
        }

        void Update()
        {
            if (material == null || session == null) return;
            // Unity can preserve scene objects when Enter Play Mode skips a domain reload.
            // Start normally creates this, but lazily restore it if the editor kept an older instance.
            if (properties == null) properties = new MaterialPropertyBlock();
            var samples = session.Data.samples; int n = samples.Length;
            if (n == 0) return;
            float dt = Mathf.Min(Time.unscaledDeltaTime, 0.05f);

            float stick = 0;
#if ENABLE_INPUT_SYSTEM
            if (hovered && Gamepad.current != null) { Vector2 v = Gamepad.current.leftStick.ReadValue(); stick = Mathf.Abs(v.x) > Mathf.Abs(v.y) ? v.x : -v.y; }
            if (hovered && UnityEngine.XR.XRSettings.isDeviceActive)
            {
                var device = UnityEngine.XR.InputDevices.GetDeviceAtXRNode(UnityEngine.XR.XRNode.RightHand);
                if (device.TryGetFeatureValue(UnityEngine.XR.CommonUsages.primary2DAxis, out Vector2 axis))
                {
                    float a = Mathf.Abs(axis.x) > Mathf.Abs(axis.y) ? axis.x : -axis.y;
                    if (Mathf.Abs(a) > Mathf.Abs(stick)) stick = a;
                }
            }
            if (hovered && Keyboard.current != null)
            {
                if (Keyboard.current.leftArrowKey.wasPressedThisFrame) Step(-1);
                if (Keyboard.current.rightArrowKey.wasPressedThisFrame) Step(1);
            }
#endif
            if (Mathf.Abs(stick) < 0.15f) stick = 0;

            // Momentum: drag moves the deck directly; otherwise it coasts with friction, then a spring settles it on
            // a card (or on the card a step, the barcode or a click asked for).
            if (stick != 0)
            {
                goal = pendingPick = -1;
                velocity = Mathf.Lerp(velocity, stick * 14f, 1f - Mathf.Exp(-dt * 8f));
                position += velocity * dt;
            }
            else if (!dragging)
            {
                if (goal >= 0 || Mathf.Abs(velocity) < SettleSpeed)
                {
                    float to = goal >= 0 ? goal : Mathf.Round(position);
                    velocity += ((to - position) * SpringK - velocity * SpringDamp) * dt;
                    if (goal >= 0 && Mathf.Abs(to - position) < 0.01f && Mathf.Abs(velocity) < 0.05f) { position = goal; velocity = 0; goal = -1; }
                }
                else velocity *= Mathf.Exp(-Friction * dt);
                position += velocity * dt;
            }
            if (position < 0) { position = 0; if (velocity < 0) velocity = 0; }
            if (position > n - 1) { position = n - 1; if (velocity > 0) velocity = 0; }

            int next = Mathf.Clamp(Mathf.RoundToInt(position), 0, n - 1);
            if (next != focus) { focus = next; Refresh(); }
            if (pendingPick >= 0 && Mathf.Abs(position - pendingPick) < 0.08f) { focus = pendingPick; pendingPick = -1; Select(); }
            // Scrolled away from the picked card: it goes back into the pile.
            if (selected >= 0 && !puttingBack && Mathf.Abs(position - selected) > 1.5f) PutBack();

            Rect r = root.rect;
            slide = Mathf.Lerp(slide, selected >= 0 && !puttingBack ? 1f : 0f, 1f - Mathf.Exp(-dt * 9f));
            deckX = Mathf.Lerp(r.width * 0.5f, (r.width - PreviewWidth - 12f) * 0.5f, slide);
            DrawDeck(samples, r);
            PlaceOverlay(n, r);
        }

        void DrawDeck(MLSample[] samples, Rect r)
        {
            int n = samples.Length, centre = Mathf.RoundToInt(position), count = 0;
            float deckY = DeckTop + DeckHeight * 0.5f;
            float clipLeft = 4f, clipRight = Mathf.Lerp(r.width - 4f, r.width - PreviewWidth - 16f, slide);
            var toWorld = root.localToWorldMatrix;
            // Far to near, so each card blends over the ones behind it.
            for (int k = Range; k >= 0; k--)
            {
                for (int side = -1; side <= 1; side += 2)
                {
                    if (k == 0 && side > 0) break;
                    int i = centre + side * k;
                    if (i < 0 || i >= n || samples[i].removed) continue;
                    float d = i - position, ad = Mathf.Abs(d), ease = Ease(ad);
                    float size = Mathf.Lerp(FocusSize, SideSize, ease), angle = Mathf.Sign(d) * Turn * ease;
                    float half = size * 0.5f * Mathf.Cos(angle * Mathf.Deg2Rad), x = deckX + CardOffset(d);
                    float alpha = Mathf.Clamp01((x - half - clipLeft) / 24f) * Mathf.Clamp01((clipRight - x - half) / 24f);
                    if (i == selected) alpha *= 0.16f;   // the picked card's ghost keeps its place in the deck
                    if (alpha <= 0.01f) continue;
                    float bright = Mathf.Lerp(1f, 0.5f, Mathf.Clamp01((ad - 0.25f) / 6f));
                    var p = new Vector3(r.xMin + x, r.yMax - deckY, -Base - Pop * (1f - ease) + Recede * Mathf.Min(ad, Range));
                    matrices[count] = toWorld * Matrix4x4.TRS(p, Quaternion.Euler(0, angle, 0), new Vector3(size, size, 1));
                    Rect uv = session.UV(i); uvs[count] = new Vector4(uv.x, uv.y, uv.width, uv.height);
                    colors[count] = new Vector4(bright, bright, bright, alpha);
                    drawn[count] = i; drawnX[count] = x; drawnHalf[count] = half; drawnAlpha[count] = alpha;
                    count++;
                }
            }
            drawnCount = count;
            if (count == 0 || !SystemInfo.supportsInstancing) return;
            properties.SetVectorArray("_UVRect", uvs);
            properties.SetVectorArray("_Color", colors);
            Graphics.DrawMeshInstanced(quad, 0, material, matrices, count, properties, ShadowCastingMode.Off, false, gameObject.layer);
        }

        // 0 for the focused card, 1 from the first neighbour out; smoothstep so cards ease into their turn.
        static float Ease(float distance) { float a = Mathf.Min(distance, 1f); return a * a * (3f - 2f * a); }
        static float CardOffset(float d)
        {
            float ad = Mathf.Abs(d);
            return Mathf.Sign(d) * (Neighbor * Ease(ad) + Spacing * Mathf.Max(0f, ad - 1f));
        }

        void PlaceOverlay(int n, Rect r)
        {
            float tagY = DeckTop + DeckHeight * 0.5f + FocusSize * 0.5f + 24f;   // clear of the focused card, which pops toward the viewer
            classTag.anchoredPosition = new Vector2(deckX - classTagWidth * 0.5f, -tagY);
            // The tag belongs to the card in front: it shows once the deck settles, never for a picked card.
            float settled = 1f - Mathf.Clamp01(Mathf.Abs(position - focus) * 3f) * 1f;
            tagFade.alpha = focus == selected ? 0f : Mathf.MoveTowards(tagFade.alpha, settled, Time.unscaledDeltaTime * 6f);

            float barWidth = bar.rectTransform.rect.width, perCard = barWidth / Mathf.Max(1, n);
            float x0 = Mathf.Clamp((position - Range) * perCard, 0f, barWidth), x1 = Mathf.Clamp((position + Range + 1f) * perCard, 0f, barWidth);
            playhead.anchoredPosition = new Vector2(x0 - 1.5f, 3f);
            playhead.sizeDelta = new Vector2(Mathf.Max(4f, x1 - x0 + 3f), BarHeight + 6f);
            focusTick.anchoredPosition = new Vector2((position + 0.5f) * perCard - 0.8f, 5f);
        }

        // The deck's focus point and the picked card's resting place, as the preview's anchored position (centre).
        Vector3 DeckPoint(float d) => new Vector3(deckX + CardOffset(d), -(DeckTop + DeckHeight * 0.5f), -Base - Pop);
        Vector3 PreviewPoint(Rect r) => new Vector3(r.width - PreviewWidth * 0.5f - 4f, -(DeckTop + PreviewHeight * 0.5f), -12f);

        public void Scrub(float delta)
        {
            if (session == null) return;
            if (Mathf.Abs(delta) >= 0.5f) Step(Mathf.RoundToInt(delta));
            else { goal = pendingPick = -1; velocity += delta * 8f; }
        }
        void Step(int delta)
        {
            int n = session.Data.samples.Length; if (n == 0) return;
            goal = Mathf.Clamp((goal >= 0 ? goal : Mathf.RoundToInt(position)) + delta, 0, n - 1); pendingPick = -1;
        }

        public void Select()
        {
            var samples = session.Data.samples;
            if (samples.Length == 0) return;
            focus = Mathf.Clamp(focus, 0, samples.Length - 1);
            if (samples[focus].removed) return;
            StopAllCoroutines(); puttingBack = false; selected = focus; goal = pendingPick = -1;
            image.texture = session.Atlas; image.uvRect = session.UV(selected); preview.gameObject.SetActive(true); Refresh();
            Vector3 from = DeckPoint(selected - position), to = PreviewPoint(root.rect);
            preview.anchoredPosition3D = from; preview.localScale = Vector3.one * 0.5f; previewFade.alpha = 0f;
            StartCoroutine(ForgeTween.Run(.32f, 0, t =>
            {
                float e = ForgeTween.EaseOutCubic(t);
                preview.localScale = Vector3.one * Mathf.Lerp(.5f, 1, e); preview.anchoredPosition3D = Vector3.Lerp(from, to, e); previewFade.alpha = Mathf.Clamp01(t * 3f);
            }));
        }
        public void PutBack()
        {
            if (selected < 0 || puttingBack) return;
            if (!isActiveAndEnabled) { FinishPutBack(); return; }
            StopAllCoroutines(); puttingBack = true;
            Vector3 from = preview.anchoredPosition3D; float scale = preview.localScale.x, fade = previewFade.alpha;
            int card = selected;
            StartCoroutine(ForgeTween.Run(.24f, 0, t =>
            {
                float e = ForgeTween.EaseInCubic(t);
                preview.localScale = Vector3.one * Mathf.Lerp(scale, .5f, e);
                preview.anchoredPosition3D = Vector3.Lerp(from, DeckPoint(card - position), e);
                previewFade.alpha = fade * (1f - t);
            }, FinishPutBack));
        }
        void FinishPutBack() { selected = -1; puttingBack = false; preview.gameObject.SetActive(false); Refresh(); }

        public void OnScroll(PointerEventData e)
        {
            Vector2 s = e.scrollDelta;
            float amount = Mathf.Abs(s.x) > Mathf.Abs(s.y) ? s.x : -s.y;
            if (amount == 0) return;
            goal = pendingPick = -1;
            // One push per wheel notch however the input module scales it; small trackpad deltas push less.
            velocity = Mathf.Clamp(velocity + Mathf.Sign(amount) * Mathf.Min(Mathf.Abs(amount), 1f) * WheelPush, -40f, 40f);
        }
        public void OnPointerDown(PointerEventData e) { dragMoved = false; dragDistance = 0; }
        public void OnBeginDrag(PointerEventData e)
        {
            if (!LocalPoint(e, out Vector2 p)) return;
            dragging = true; goal = pendingPick = -1; velocity = dragVelocity = 0; lastDragX = p.x; lastDragTime = Time.unscaledTime;
        }
        public void OnDrag(PointerEventData e)
        {
            if (!dragging || !LocalPoint(e, out Vector2 p)) return;
            float dx = p.x - lastDragX; lastDragX = p.x;
            dragDistance += Mathf.Abs(dx); if (dragDistance > 4f) dragMoved = true;
            // The deck follows the pointer; the speed it had when let go becomes its momentum.
            float delta = -dx / MmPerCard, now = Time.unscaledTime;
            position = Mathf.Clamp(position + delta, 0, Mathf.Max(0, session.Data.samples.Length - 1));
            dragVelocity = Mathf.Lerp(dragVelocity, delta / Mathf.Max(now - lastDragTime, 1e-3f), 0.35f); lastDragTime = now;
        }
        public void OnEndDrag(PointerEventData e)
        {
            if (!dragging) return;
            dragging = false;
            velocity = Time.unscaledTime - lastDragTime > 0.1f ? 0f : Mathf.Clamp(dragVelocity, -30f, 30f);
        }
        public void OnPointerEnter(PointerEventData e) => hovered = true;
        public void OnPointerExit(PointerEventData e) => hovered = false;
        public void OnPointerClick(PointerEventData e)
        {
            if (dragMoved || !LocalPoint(e, out Vector2 p)) return;
            float deckY = DeckTop + DeckHeight * 0.5f;
            if (Mathf.Abs(p.y - deckY) > FocusSize * 0.5f) return;
            // Nearest card first: instances were drawn far to near.
            int hit = -1;
            for (int k = drawnCount - 1; k >= 0; k--)
                if (drawnAlpha[k] > 0.4f && Mathf.Abs(p.x - drawnX[k]) <= drawnHalf[k]) { hit = drawn[k]; break; }
            if (hit < 0) return;
            if (selected >= 0 && hit != selected) PutBack();
            if (hit == focus && Mathf.Abs(position - focus) < 0.3f) Select();
            else { goal = hit; pendingPick = hit; }
        }
        public void ScrubBar(PointerEventData e)
        {
            var hit = e.pointerCurrentRaycast;
            int n = session.Data.samples.Length;
            if (hit.gameObject == null || n == 0) return;
            var rt = bar.rectTransform; Vector3 local = rt.InverseTransformPoint(hit.worldPosition);
            float f = Mathf.Clamp01((local.x - rt.rect.xMin) / rt.rect.width);
            goal = Mathf.Clamp(Mathf.FloorToInt(f * n), 0, n - 1); pendingPick = -1; velocity = 0;
        }
        // The pointer on the panel, from where the ray or mouse hit it (top-left mm). Screen positions don't work
        // for rays: PointableCanvasModule reports them from a stand-in camera.
        bool LocalPoint(PointerEventData e, out Vector2 point)
        {
            point = Vector2.zero;
            var hit = e.pointerCurrentRaycast;
            if (hit.gameObject == null) return false;
            Vector3 local = root.InverseTransformPoint(hit.worldPosition);
            point = new Vector2(local.x - root.rect.xMin, root.rect.yMax - local.y);
            return true;
        }

        void UpdateReturnInput()
        {
#if ENABLE_INPUT_SYSTEM
            if (Keyboard.current != null && Keyboard.current.xKey.wasPressedThisFrame && selected >= 0) PutBack();
            if (UnityEngine.XR.XRSettings.isDeviceActive)
            {
                var device = UnityEngine.XR.InputDevices.GetDeviceAtXRNode(UnityEngine.XR.XRNode.RightHand);
                if (device.TryGetFeatureValue(UnityEngine.XR.CommonUsages.secondaryButton, out bool pressed) && pressed && selected >= 0) PutBack();
            }
#else
            if (Input.GetKeyDown(KeyCode.X) && selected >= 0) PutBack();
#endif
        }
        void LateUpdate() => UpdateReturnInput();
        // Layout swaps deactivate the panel mid-animation: land the picked card where it was going.
        void OnDisable()
        {
            dragging = false;
            if (puttingBack) FinishPutBack();
            else if (selected >= 0 && preview != null && root != null)
            { preview.localScale = Vector3.one; previewFade.alpha = 1f; preview.anchoredPosition3D = PreviewPoint(root.rect); }
        }
        void OnDestroy()
        {
            if (session != null) { session.SamplesChanged -= OnSamplesChanged; session.Reloaded -= OnReloaded; }
            if (material != null) Destroy(material);
        }
    }
}
