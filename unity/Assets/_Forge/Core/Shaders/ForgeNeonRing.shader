// A ring of white light (a dock button or a volume base): white-hot core line, white glows, a faint mode-hue halo.
// Hover runs a comet of light around its rim; selected fills it with a soft disc. _Outline draws only a thin ring, for the
// hologram column under a raised button (one quad per ring, alpha in the vertex color).
Shader "Forge/NeonRing"
{
    Properties
    {
        _Size ("Quad size (mm)", Float) = 132
        _Radius ("Ring radius (mm)", Float) = 36
        _Hue ("Mode hue", Color) = (1, 1, 1, 1)
        _HueT ("Hue strength", Float) = 0.1
        _Near ("Pointer nearness", Range(0, 1)) = 0
        _Hover ("Hover comet", Range(0, 1)) = 0
        _Selected ("Selected", Range(0, 1)) = 0
        _Enabled ("Enabled", Range(0, 1)) = 1
        _Fade ("Fade", Range(0, 1)) = 1
        _Outline ("Outline only", Float) = 0
        _Spin ("Comet turn time (s)", Float) = 1.05
    }
    SubShader
    {
        Tags { "RenderType" = "Transparent" "Queue" = "Transparent+13" "RenderPipeline" = "UniversalPipeline" "IgnoreProjector" = "True" "PreviewType" = "Plane" }
        Pass
        {
            Blend One OneMinusSrcAlpha
            ZWrite Off
            Cull Off

            HLSLPROGRAM
            #pragma vertex ForgeVert
            #pragma fragment Frag
            #pragma multi_compile_instancing
            #include "ForgeLight.hlsl"

            CBUFFER_START(UnityPerMaterial)
            float4 _Hue;
            float _Size, _Radius, _HueT, _Near, _Hover, _Selected, _Enabled, _Fade, _Outline, _Spin;
            CBUFFER_END

            // The comet: conic gradient clockwise from the top (CSS conic-gradient, premultiplied):
            // clear until 210 deg, mode hue at 300, white at 352, clear again at 360.
            float4 Comet(float u, float3 hue, float hueC)
            {
                float4 c1 = float4(hue * hueC, hueC);
                if (u < 210.0 / 360.0) return 0;
                if (u < 300.0 / 360.0) return c1 * ((u - 210.0 / 360.0) / (90.0 / 360.0));
                if (u < 352.0 / 360.0) return lerp(c1, float4(1, 1, 1, 1), (u - 300.0 / 360.0) / (52.0 / 360.0));
                return float4(1, 1, 1, 1) * (1.0 - (u - 352.0 / 360.0) / (8.0 / 360.0));
            }

            half4 Frag(Varyings i) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);
                float2 p = (i.uv - 0.5) * _Size;   // mm from the center, +y = the far side
                float r = length(p);
                float aa = max(fwidth(r), 1e-3);
                float R = _Radius;
                float d = r - R;                   // + outside the circle
                float4 acc = 0;

                if (_Outline > 0.5)
                {
                    // Column ring: a 0.8 mm line just outside R at 0.4 and a soft glow.
                    Over(acc, 1, 0.14 / (1.0 + exp(3.404 * abs(d - 0.4) / 8.2)) * 2.0);
                    Over(acc, 1, 0.4 * Band(d - 0.4, 0.4, aa));
                    return half4(acc * i.color.a * _Selected * _Fade);
                }

                float hueB = min(1.0, 2.2 * _HueT), hueC = min(1.0, 0.45 + 3.0 * _HueT);
                float3 hue = _Hue.rgb;

                // Comet glow: the conic light concentrated on the ring line, a Gaussian band (3 mm inside, 5.5 mm
                // outside) around the core, so the glow travels round the rim instead of sweeping the disc.
                float u = frac(atan2(p.x, p.y) / 6.2831853 - _Time.y / _Spin);
                float4 comet = Comet(u, hue, hueC);
                float dr = r - (R - 0.8);
                float sigma = dr < 0.0 ? 3.0 : 5.5;
                OverPremul(acc, comet * exp(-0.5 * dr * dr / (sigma * sigma)) * 0.62 * _Hover);

                // Fill disc (selected): bright near the top-middle, fading out, mode hue at the rim.
                float discMask = 1.0 - smoothstep(R - 1.6 - aa, R - 1.6 + aa, r);
                float s = length(p - float2(0.0, 0.3 * R)) / (1.52 * R);
                float4 disc = s < 0.55 ? lerp(float4(0.26, 0.26, 0.26, 0.26), float4(0.06, 0.06, 0.06, 0.06), s / 0.55)
                                       : lerp(float4(0.06, 0.06, 0.06, 0.06), float4(hue * hueB, hueB), saturate((s - 0.55) / 0.45));
                OverPremul(acc, disc * discMask * _Selected);

                // The ring: halo, glows, inner glow, then the white-hot core on the inside edge.
                float4 ring = 0;
                Over(ring, hue, OuterGlow(d, hueB, 23.0, 3.3));
                Over(ring, 1, OuterGlow(d, 0.32, 13.0, 1.6));
                Over(ring, 1, OuterGlow(d, 0.7, 4.1, 0.8));
                Over(ring, 1, InsetGlow(d, 0.3, 8.2));
                Over(ring, 1, 0.95 * Band(d + 0.8, 0.8, aa));
                float ringOpacity = lerp(0.6 + 0.4 * _Near, 1.0, _Selected) * lerp(0.25, 1.0, _Enabled);
                OverPremul(acc, ring * ringOpacity);

                // Comet line: a 3.3 mm band riding the core.
                float band = smoothstep(R - 3.3, R - 2.5, r) * (1.0 - smoothstep(R + 0.8, R + 1.6, r));
                OverPremul(acc, comet * band * _Hover);

                return half4(saturate(acc) * _Fade);
            }
            ENDHLSL
        }
    }
}
