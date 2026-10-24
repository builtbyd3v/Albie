using TMPro;
using UnityEditor;
using UnityEngine;
using UnityEngine.TextCore.LowLevel;

/// <summary>
/// Forge > Build Look Assets: the assets <see cref="ForgeTheme"/> loads from Core/Resources/ForgeLook.
/// Inter as a static TMP SDF font (ASCII and a few symbols), Inter with a glow for text drawn as light, and the light
/// materials for the Forge/* shaders (render queues set so the layers stack: frame, pool, column, ring, label).
/// Creates what's missing and refreshes material settings; never touches the TMP default font.
/// </summary>
public static class ForgeLookBuilder
{
    public const string Dir = "Assets/_Forge/Core/Resources/ForgeLook";
    const string FontSource = "Assets/_Forge/Core/Fonts/Inter-Regular.ttf";
    const string Characters =
        " !\"#$%&'()*+,-./0123456789:;<=>?@ABCDEFGHIJKLMNOPQRSTUVWXYZ[\\]^_`abcdefghijklmnopqrstuvwxyz{|}~" +
        "°±·×–—‘’“”•…‹›←↑→↓−✓";

    [MenuItem("Forge/Build Look Assets")]
    public static void Build()
    {
        EnsureFolder(Dir);
        var font = BuildFont();
        // Inter ships Regular only, so bold is a dilation; this much reads as semibold (panel titles), not heavy.
        font.material.SetFloat("_WeightBold", 0.35f);
        EditorUtility.SetDirty(font.material);
        BuildGlowText(font);
        Light("NeonFrame", "Forge/NeonFrame", 3010);
        Light("LightPool", "Forge/LightPool", 3011);
        Light("NeonColumn", "Forge/NeonRing", 3012).SetFloat("_Outline", 1f);
        Light("NeonRing", "Forge/NeonRing", 3013).SetFloat("_Outline", 0f);
        if (Shader.Find("Forge/PanelSurface") != null) Light("PanelSurface", "Forge/PanelSurface", 3000);
        AssetDatabase.SaveAssets();
        Debug.Log("[ForgeLookBuilder] Look assets ready in " + Dir);
    }

    static TMP_FontAsset BuildFont()
    {
        string path = Dir + "/Inter SDF.asset";
        var font = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(path);
        if (font != null) return font;

        var ttf = AssetDatabase.LoadAssetAtPath<Font>(FontSource);
        // 64 pt samples with 16 px padding: room for the label glow (a quarter em).
        font = TMP_FontAsset.CreateFontAsset(ttf, 64, 16, GlyphRenderMode.SDFAA, 1024, 1024, AtlasPopulationMode.Dynamic, false);
        font.name = "Inter SDF";
        if (!font.TryAddCharacters(Characters, out string missing) && !string.IsNullOrEmpty(missing))
            Debug.LogWarning("[ForgeLookBuilder] Inter has no glyph for: " + missing);
        font.atlasPopulationMode = AtlasPopulationMode.Static;
        AssetDatabase.CreateAsset(font, path);
        font.atlasTexture.name = "Inter SDF Atlas";
        AssetDatabase.AddObjectToAsset(font.atlasTexture, font);
        font.material.name = "Inter SDF Material";
        AssetDatabase.AddObjectToAsset(font.material, font);
        EditorUtility.SetDirty(font);
        AssetDatabase.SaveAssets();
        return font;
    }

    // White glow hugging the letters plus a soft halo (the underlay, which unlike the glow also adds alpha, so it
    // shows over passthrough). DeskDock tints the halo with the mode hue per label.
    static void BuildGlowText(TMP_FontAsset font)
    {
        string path = Dir + "/Inter Glow.mat";
        var shader = Shader.Find("TextMeshPro/Distance Field");
        var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
        bool create = mat == null;
        if (create) mat = new Material(shader);
        mat.shader = shader;
        mat.CopyPropertiesFromMaterial(font.material);
        mat.shader = shader;
        mat.SetTexture("_MainTex", font.atlasTexture);
        mat.EnableKeyword("GLOW_ON");
        mat.SetColor("_GlowColor", new Color(1f, 1f, 1f, 0.55f));
        mat.SetFloat("_GlowOffset", 0f);
        mat.SetFloat("_GlowInner", 0.05f);
        mat.SetFloat("_GlowOuter", 0.35f);
        mat.SetFloat("_GlowPower", 0.6f);
        mat.EnableKeyword("UNDERLAY_ON");
        mat.SetColor("_UnderlayColor", new Color(1f, 1f, 1f, 0.25f));
        mat.SetFloat("_UnderlayOffsetX", 0f);
        mat.SetFloat("_UnderlayOffsetY", 0f);
        mat.SetFloat("_UnderlayDilate", 0.15f);
        mat.SetFloat("_UnderlaySoftness", 0.85f);
        mat.SetFloat("_WeightBold", 0.35f);  // Inter ships Regular only; this reads as semibold, not heavy
        ShaderUtilities.UpdateShaderRatios(mat);
        mat.renderQueue = 3014;
        if (create) AssetDatabase.CreateAsset(mat, path);
        else EditorUtility.SetDirty(mat);
    }

    static Material Light(string name, string shaderName, int queue)
    {
        string path = $"{Dir}/{name}.mat";
        var shader = Shader.Find(shaderName);
        var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (mat == null)
        {
            mat = new Material(shader);
            AssetDatabase.CreateAsset(mat, path);
        }
        mat.shader = shader;
        mat.renderQueue = queue;
        mat.enableInstancing = true;
        EditorUtility.SetDirty(mat);
        return mat;
    }

    static void EnsureFolder(string path)
    {
        if (AssetDatabase.IsValidFolder(path)) return;
        int slash = path.LastIndexOf('/');
        EnsureFolder(path.Substring(0, slash));
        AssetDatabase.CreateFolder(path.Substring(0, slash), path.Substring(slash + 1));
    }
}
