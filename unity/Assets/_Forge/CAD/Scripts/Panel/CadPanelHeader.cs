using System.Collections.Generic;
using TMPro;
using UnityEngine;

/// <summary>
/// The design panel's header (CAD UX spec 2.1), over Core's: the design's glyph on the mode tile (Core's badge, its
/// "CAD" text hidden: the orange tile already says CAD), the title as the Designs menu button with a chevron, and the
/// subtitle's hub dot ("v4 · 4 versions · Live"; amber "Offline, stored versions"; a pulsing orange dot while
/// "Building v5…"). Core keeps the title and subtitle text (<see cref="ForgePanel.Title"/> / Subtitle) and the
/// Straight / Face me pills; this draws on a layer above them, so no Core file changes.
/// The Designs menu: a 360 mm popover listing the hub's library (refreshed each time it opens).
/// </summary>
public sealed class CadPanelHeader
{
    // Board numbers (mm from the panel's top left).
    const float Header = 64f, MenuX = 58f, MenuY = 58f, MenuWidth = 360f, GlyphSize = 20f;
    const float DotSize = 7f, DotGap = 6f, ChevronSize = 12f;

    readonly CadParamsPanel owner;
    readonly ForgePanel panel;
    readonly RectTransform overlay;
    readonly TMP_Text coreTitle, coreSubtitle;
    readonly RectTransform coreBadge;
    CadShape glyph;
    string glyphName;
    CadButton titleButton;
    CadShape chevron;
    ForgeSurface hubDot;
    RectTransform menu, blocker;
    readonly List<CadButton> menuItems = new List<CadButton>();
    readonly List<string> menuGlyphs = new List<string>();
    TMP_Text menuHead, menuFoot;
    bool busy;
    float pulse;
    string subtitle;

    public bool MenuOpen => menu != null && menu.gameObject.activeSelf;

    public CadPanelHeader(CadParamsPanel owner, ForgePanel panel, RectTransform overlay)
    {
        this.owner = owner;
        this.panel = panel;
        this.overlay = overlay;
        var bar = panel.transform.Find("TitleBar");
        coreTitle = bar != null ? bar.Find("Title")?.GetComponent<TMP_Text>() : null;
        coreSubtitle = bar != null ? bar.Find("Subtitle")?.GetComponent<TMP_Text>() : null;
        coreBadge = bar != null ? bar.Find("Badge") as RectTransform : null;
        Build();
    }

    void Build()
    {
        // The glyph tile: on Core's badge (it follows Core's layout), opaque so the mode text never shows through.
        RectTransform tileRoot;
        if (coreBadge != null)
        {
            tileRoot = CadUi.Rect(coreBadge, "CadGlyph");
            CadUi.Stretch(tileRoot, 0f, 0f, 0f, 0f);
        }
        else
        {
            tileRoot = CadUi.Rect(overlay, "CadGlyph");
            CadUi.Place(tileRoot, 20f, 12f, 40f, 40f);
        }
        // The CAD hue at 16% over the panel's graphite, as one opaque colour, then the hue's 35% outline on top.
        var top = ForgeTheme.PanelTop;
        var hue = CadUi.Hue;
        var tile = CadUi.Surface(tileRoot, "Tile", new Color(Mathf.Lerp(top.r, hue.r, 0.16f), Mathf.Lerp(top.g, hue.g, 0.16f),
            Mathf.Lerp(top.b, hue.b, 0.16f), 1f), 12f);
        CadUi.Stretch(tile.rectTransform, 0f, 0f, 0f, 0f);
        var rim = CadUi.Surface(tileRoot, "Rim", CadUi.HueA(0f), 12f, 0.35f);
        CadUi.Stretch(rim.rectTransform, 0f, 0f, 0f, 0f);
        glyph = CadUi.Shape(tileRoot, "Glyph", s =>
        {
            float x = (s.Width - GlyphSize) * 0.5f, y = (s.Height - GlyphSize) * 0.5f;
            CadIcons.Glyph(s, glyphName, x, y, GlyphSize, CadUi.Peach);
        });
        CadUi.Stretch(glyph.rectTransform, 0f, 0f, 0f, 0f);

        // The title is the Designs button: an invisible pill over Core's title and subtitle, lit when pointed at.
        titleButton = CadButton.Create(overlay, "TitleButton", CadButton.Ghost, 12f, owner.ToggleMenu);
        chevron = CadUi.Shape(overlay, "Chevron", s => CadIcons.Chevron(s, 0f, 0f, ChevronSize, CadUi.White(0.55f)));
        hubDot = CadUi.Pill(overlay, "HubDot", CadUi.HubLive, glow: 0.45f);

        // Designs menu (and a catcher behind it: a press anywhere else closes it).
        var blockHit = CadUi.Hit(overlay, "MenuBlocker");
        blocker = blockHit.rectTransform;
        CadUi.Stretch(blocker, -40f, -40f, -40f, -40f);
        var blockButton = blocker.gameObject.AddComponent<UnityEngine.UI.Button>();
        blockButton.transition = UnityEngine.UI.Selectable.Transition.None;
        blockButton.onClick.AddListener(() => owner.ShowMenu(false));
        blocker.gameObject.SetActive(false);

        var menuSurface = CadUi.Surface(overlay, "DesignsMenu", CadUi.MenuFill, 18f, 0.09f);
        menu = menuSurface.rectTransform;
        menuSurface.raycastTarget = true;  // presses on the menu's padding don't fall through to the catcher
        CadUi.Place(menu, MenuX, MenuY, MenuWidth, 200f);
        menuHead = CadUi.Text(menu, "Head", CadUi.Overline, CadUi.White(0.45f));
        menuHead.text = "Designs · from hub/cad/parts";
        menuFoot = CadUi.Text(menu, "Foot", CadUi.Overline, CadUi.White(0.45f));
        menuFoot.text = "A new file in hub/cad/parts shows up here.";
        menu.gameObject.SetActive(false);
    }

    /// <summary>Title, subtitle, glyph and hub dot for this state.</summary>
    public void Show(CadPanelState s)
    {
        string title = string.IsNullOrEmpty(s.title) ? "Design" : s.title;
        if (panel.Title != title) panel.Title = title;
        string g = string.IsNullOrEmpty(s.glyph) ? "box" : s.glyph;
        if (g != glyphName)
        {
            glyphName = g;
            glyph.Redraw();
        }

        string line;
        if (!s.ready) line = "Connecting to the hub…";
        else if (s.building) line = "Building v" + s.buildingVersion + "…";
        else
        {
            int count = s.versions.Count;
            string where = s.mock ? "Offline, stored versions" : s.live ? "Live" : "Hub lost, retrying";
            line = "v" + s.version + (count > 0 ? " · " + count + (count == 1 ? " version" : " versions") : "") + " · " + where;
        }
        busy = s.building;
        hubDot.color = busy ? CadUi.Hue : !s.ready ? ForgeTheme.HubConnecting : s.mock || !s.live ? CadUi.HubOff : CadUi.HubLive;
        if (!busy) hubDot.canvasRenderer.SetAlpha(1f);
        // Room for the dot before the text (Core's subtitle is 11.5 mm: 1.13 em is the dot and its gap).
        string text = "<space=1.13em>" + line;
        if (text != subtitle)
        {
            subtitle = text;
            panel.Subtitle = text;
        }
        Layout(title, line);
    }

    // Core lays the title and subtitle out after its badge; the button, chevron and dot follow them.
    void Layout(string title, string line)
    {
        float left = 72f;
        if (coreTitle != null)
        {
            var rt = coreTitle.rectTransform;
            left = rt.offsetMin.x;  // Core's PlaceInHeader: anchors 0..1, offsetMin.x = left
        }
        float titleWidth = coreTitle != null ? CadUi.Width(coreTitle, title) : 120f;
        float subWidth = coreSubtitle != null ? CadUi.Width(coreSubtitle, "<space=1.13em>" + line) : 160f;
        float maxWidth = panel.SizeMeters.x / ForgePanel.MetersPerUnit - left - 190f;  // clear of Straight / Face me
        titleWidth = Mathf.Min(titleWidth, maxWidth - ChevronSize - 6f);
        float right = Mathf.Min(maxWidth, Mathf.Max(titleWidth + 6f + ChevronSize, subWidth));
        CadUi.Place(titleButton.Rect, left - 8f, 10f, right + 16f, 44f);
        CadUi.Place(chevron.rectTransform, left + titleWidth + 6f, 25f - ChevronSize * 0.5f + 1f, ChevronSize, ChevronSize);
        CadUi.Place(hubDot.rectTransform, left + 0.5f, 44f - DotSize * 0.5f, DotSize, DotSize);
    }

    /// <summary>The busy dot's pulse (1 s, ease in-out).</summary>
    public void Tick(float dt)
    {
        if (!busy) return;
        pulse += dt;
        float a = 0.45f + 0.55f * (0.5f + 0.5f * Mathf.Cos(pulse * 2f * Mathf.PI));
        hubDot.canvasRenderer.SetAlpha(a);
    }

    /// <summary>
    /// The mode tile's text off (call after Workspace has set the badge: Start). The tile stays Core's; the glyph is
    /// drawn over it.
    /// </summary>
    public void HideModeText()
    {
        panel.SetBadge(" ", ForgeTheme.CAD);
    }

    // ---- Designs menu ----

    public void ShowMenu(bool open, CadPanelState s)
    {
        if (open) RebuildMenu(s);
        menu.gameObject.SetActive(open);
        blocker.gameObject.SetActive(open);
        if (open)
        {
            blocker.SetAsLastSibling();
            menu.SetAsLastSibling();
        }
    }

    /// <summary>One row per design in the library: glyph, title, two lines of description, "open · v5" or its version.</summary>
    public void RebuildMenu(CadPanelState s)
    {
        var parts = s.session != null ? s.session.Parts.parts : null;
        int count = parts != null ? parts.Count : 0;
        while (menuItems.Count < count) menuItems.Add(MenuItem(menuItems.Count));
        float y = 6f + 26f;
        CadUi.Place(menuHead.rectTransform, 16f, 8f, MenuWidth - 32f, 18f);
        for (int i = 0; i < menuItems.Count; i++)
        {
            var item = menuItems[i];
            bool used = i < count;
            CadUi.Show(item, used);
            if (!used) continue;
            var part = parts[i];
            bool open = part.part == s.p?.part || part.active;
            string title = string.IsNullOrEmpty(part.title) ? CadLook.Pretty(part.part) : part.title;
            string detail = CadUi.Clean(part.error ?? part.description ?? "");
            string tag = open ? "open · v" + s.version : part.version.HasValue ? "v" + part.version.Value : "new";
            var texts = item.GetComponentsInChildren<TMP_Text>(true);  // menu only: built on open, not per frame
            texts[0].text = title;
            texts[1].text = detail;
            texts[2].text = tag;
            texts[1].color = part.error != null ? CadUi.BadText : CadUi.White(0.5f);
            item.On = open;
            item.Interactable = part.error == null;
            menuGlyphs[i] = part.glyph;
            item.GetComponentInChildren<CadShape>(true).Redraw();
            float tagWidth = CadUi.Width(texts[2], tag);
            CadUi.PlaceRight(texts[2].rectTransform, 10f, 9f, tagWidth + 2f, 18f);
            float textWidth = MenuWidth - 12f - 20f - 34f - 10f - 10f - tagWidth;
            CadUi.Place(texts[0].rectTransform, 54f, 8f, textWidth, 19f);
            float descHeight = Mathf.Min(CadUi.Height(texts[1], detail, textWidth), CadUi.Small * 1.35f * 2f + 1f);
            CadUi.Place(texts[1].rectTransform, 54f, 28f, textWidth, descHeight);
            float h = Mathf.Max(52f, 28f + descHeight + 9f);
            CadUi.Place(item.Rect, 6f, y, MenuWidth - 12f, h);
            y += h + 2f;
        }
        CadUi.Place(menuFoot.rectTransform, 16f, y + 4f, MenuWidth - 32f, 18f);
        y += 4f + 18f + 8f;
        CadUi.Place(menu, MenuX, MenuY, MenuWidth, y);
    }

    CadButton MenuItem(int index)
    {
        menuGlyphs.Add(null);
        var item = CadButton.Create(menu, "Design " + index, CadButton.Row, 12f, () => owner.OpenDesign(index));
        var tile = CadUi.Surface(item.transform, "Tile", CadUi.HueA(0.14f), 10f);
        CadUi.Place(tile.rectTransform, 10f, 9f, 34f, 34f);
        int i = index;
        var icon = CadUi.Shape(tile.transform, "Glyph", s => CadIcons.Glyph(s, menuGlyphs[i], (s.Width - 20f) * 0.5f, (s.Height - 20f) * 0.5f, 20f, CadUi.Peach));
        CadUi.Stretch(icon.rectTransform, 0f, 0f, 0f, 0f);
        CadUi.Text(item.transform, "Title", 13.5f, CadUi.White(0.95f), bold: true);
        var desc = CadUi.Text(item.transform, "Description", CadUi.Small, CadUi.White(0.5f), wrap: true);
        desc.alignment = TextAlignmentOptions.TopLeft;
        desc.overflowMode = TextOverflowModes.Ellipsis;
        desc.maxVisibleLines = 2;
        CadUi.LineHeight(desc, 1.35f);
        CadUi.Text(item.transform, "Tag", CadUi.Overline, CadUi.White(0.55f), align: TextAlignmentOptions.Right);
        return item;
    }
}
