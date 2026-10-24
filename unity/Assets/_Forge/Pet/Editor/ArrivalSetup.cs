// ArrivalSetup.cs — the first-arrival greeting pieces PetRuntimeSetup builds into Prefabs/PetRuntime.prefab:
//
//   PetRuntime/ArrivalGreeting (saved inactive; ArrivalGreeting + ArrivalDirector drive it)
//   ├─ GreetingCard     world-space canvas (1 unit = 1 mm, 300 x 110): a light translucent rounded sheet with a soft shadow,
//   │                   a MSG-hue accent strip and the headline in Inter (ForgeTheme.Font / PanelText): the digest line.
//   └─ Ring START       one of Hudson's dock rings (Core DockRing.Create: neon ring + glowing label, hue ForgeTheme.Code,
//                       90 mm) with its invisible uGUI Button, plus Interaction SDK ray + poke canvas interaction added
//                       through the SDK's QuickActions (looked up by name, so this compiles without the SDK; the mouse
//                       always works through the canvas' GraphicRaycaster and DesktopRig).
// Idempotent (PetRuntimeSetup rebuilds the whole prefab). No Meta types referenced here, per the Pet rules.

using System;
using System.Reflection;
using TMPro;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace Forge.Pet.Editor
{
    public static class ArrivalSetup
    {
        static readonly Vector2 CardMm = new Vector2(300f, 110f);
        const float RingMm = 90f;
        // The Messages cards' light fill (rest alpha 0.8) and the MSG hue for the strip; text colours from ForgeTheme.
        static readonly Color CardFill = new Color(0.12f, 0.15f, 0.21f, 0.8f);
        static readonly Color Shadow = new Color(0f, 0f, 0f, 0.25f);

        /// The greeting (card + START ring) under `parent`, inactive. Returns the ArrivalGreeting on it.
        internal static ArrivalGreeting CreateGreeting(Transform parent)
        {
            var go = new GameObject("ArrivalGreeting");
            go.transform.SetParent(parent, false);
            WhistleSetup.RegisterCreated(go, "Create ArrivalGreeting");

            // ---- Card ----
            var card = new GameObject("GreetingCard", typeof(RectTransform), typeof(Canvas), typeof(CanvasGroup));
            card.transform.SetParent(go.transform, false);
            card.GetComponent<Canvas>().renderMode = RenderMode.WorldSpace;
            var rt = (RectTransform)card.transform;
            rt.sizeDelta = CardMm;
            rt.localScale = Vector3.one * ForgePanel.MetersPerUnit;

            Image sheet = NewImage("Sheet", rt, Rounded(), CardFill);
            Stretch(sheet.rectTransform, 0f, 0f, 0f, 0f);
            var shadow = sheet.gameObject.AddComponent<Shadow>();
            shadow.effectColor = Shadow;
            shadow.effectDistance = new Vector2(0f, -4f);

            Image accent = NewImage("Accent", rt, Rounded(), ForgeTheme.Messages);
            var accentRt = accent.rectTransform;
            accentRt.anchorMin = new Vector2(0f, 0.18f);
            accentRt.anchorMax = new Vector2(0f, 0.82f);
            accentRt.pivot = new Vector2(0f, 0.5f);
            accentRt.sizeDelta = new Vector2(5f, 0f);
            accentRt.anchoredPosition = new Vector2(10f, 0f);

            TMP_Text headline = NewText("Headline", rt, "You have no new messages today.", 21f, TextAlignmentOptions.MidlineLeft);
            headline.fontStyle = FontStyles.Bold;
            headline.textWrappingMode = TextWrappingModes.Normal;
            headline.overflowMode = TextOverflowModes.Ellipsis;
            headline.color = ForgeTheme.PanelText;
            Stretch(headline.rectTransform, 28f, 14f, 18f, 14f);

            // ---- START ring (Core's dock ring, on its own) ----
            DockRing ring = DockRing.Create(go.transform, "START", ForgeTheme.Code, RingMm, null);
            ring.transform.localPosition = new Vector3(0.25f, 0f, 0f);   // placed for real at runtime (ArrivalGreeting.Show)

            var greeting = go.AddComponent<ArrivalGreeting>();
            WhistleSetup.SetRef(greeting, "card", rt);
            WhistleSetup.SetRef(greeting, "headline", headline);
            WhistleSetup.SetRef(greeting, "startRing", ring);
            go.SetActive(false);
            return greeting;
        }

        /// Interaction SDK ray + poke on the START ring's canvas (QuickActionsAPI.AddRayCanvasInteraction /
        /// AddPokeCanvasInteraction, by name). Main's EventSystem has the PointableCanvasModule, so it isn't added here.
        internal static string AddCanvasInteraction(ArrivalGreeting greeting)
        {
            if (greeting == null || greeting.StartRing == null) return "no ring";
            Type api = FindStaticType("Oculus.Interaction.Editor.QuickActions.QuickActionsAPI");
            if (api == null)
            {
                Debug.LogWarning("[ArrivalSetup] Interaction SDK QuickActions not found; the START ring works with the mouse only.");
                return "no Interaction SDK";
            }
            GameObject canvas = greeting.StartRing.gameObject;
            bool wasActive = greeting.gameObject.activeSelf;
            greeting.gameObject.SetActive(true);   // the wizards read the canvas' rect; an inactive target can trip them up
            try
            {
                Invoke(api, "AddRayCanvasInteraction", canvas);
                Invoke(api, "AddPokeCanvasInteraction", canvas);
            }
            catch (Exception e)
            {
                Debug.LogWarning("[ArrivalSetup] Interaction SDK canvas interaction failed on the START ring (" + e.GetType().Name + ": " + e.Message
                                 + "). Select PetRuntime/ArrivalGreeting/Ring START and add it with the Interaction SDK Quick Actions; the mouse works regardless.");
                return "no Interaction SDK (see warning)";
            }
            finally
            {
                greeting.gameObject.SetActive(wasActive);
            }
            return "poke + ray";
        }

        static void Invoke(Type api, string method, GameObject target)
        {
            MethodInfo m = api.GetMethod(method, BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(GameObject), typeof(bool) }, null);
            if (m != null) { m.Invoke(null, new object[] { target, false }); return; }
            m = api.GetMethod(method, BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(GameObject) }, null);
            if (m == null) throw new MissingMethodException(api.FullName, method);
            m.Invoke(null, new object[] { target });
        }

        // QuickActionsAPI is a static class (not a Component), so WhistleSetup.FindType can't see it.
        static Type FindStaticType(string fullName)
        {
            foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type t = null;
                try { t = a.GetType(fullName, false); } catch (Exception) { }
                if (t != null) return t;
            }
            return null;
        }

        // ---- uGUI helpers (same shapes as the Core / Messages builders) ----

        static Image NewImage(string name, Transform parent, Sprite sprite, Color color)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Image));
            go.transform.SetParent(parent, false);
            var image = go.GetComponent<Image>();
            image.sprite = sprite;
            image.type = sprite != null ? Image.Type.Sliced : Image.Type.Simple;
            image.pixelsPerUnitMultiplier = 0.6f;
            image.color = color;
            image.raycastTarget = false;
            return image;
        }

        static TMP_Text NewText(string name, Transform parent, string text, float size, TextAlignmentOptions align)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(TextMeshProUGUI));
            go.transform.SetParent(parent, false);
            var tmp = go.GetComponent<TextMeshProUGUI>();
            if (ForgeTheme.Font != null)
            {
                tmp.font = ForgeTheme.Font;
                tmp.fontSharedMaterial = ForgeTheme.Font.material;
            }
            tmp.text = text;
            tmp.fontSize = size;
            tmp.alignment = align;
            tmp.color = ForgeTheme.PanelText;
            tmp.raycastTarget = false;
            tmp.textWrappingMode = TextWrappingModes.NoWrap;
            return tmp;
        }

        static void Stretch(RectTransform rt, float left, float bottom, float right, float top)
        {
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = new Vector2(left, bottom);
            rt.offsetMax = new Vector2(-right, -top);
        }

        static Sprite Rounded() => AssetDatabase.GetBuiltinExtraResource<Sprite>("UI/Skin/UISprite.psd");
    }
}
