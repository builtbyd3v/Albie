// The light a ring casts on the desk: a warm-white pool tinted with the mode hue at its edge. _Imprint adds the faint
// ring left on the desk where a raised button lifted from.
Shader "Forge/LightPool"
{
    Properties
    {
        _Size ("Quad size (mm)", Float) = 180
        _Radius ("Ring radius (mm)", Float) = 36
        _Hue ("Mode hue", Color) = (1, 1, 1, 1)
        _HueT ("Hue strength", Float) = 0.1
        _Opacity ("Opacity", Range(0, 1)) = 0.3
        _Imprint ("Imprint ring", Range(0, 1)) = 0
        _Fade ("Fade", Range(0, 1)) = 1
    }
    SubShader
    {
        Tags { "RenderType" = "Transparent" "Queue" = "Transparent+11" "RenderPipeline" = "UniversalPipeline" "IgnoreProjector" = "True" "PreviewType" = "Plane" }
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
            float _Size, _Radius, _HueT, _Opacity, _Imprint, _Fade;
            CBUFFER_END

            half4 Frag(Varyings i) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);
                float2 p = (i.uv - 0.5) * _Size;
                float r = length(p);
                float aa = max(fwidth(r), 1e-3);
                float R = _Radius;
                float4 acc = 0;

                // Pool: warm white at the center, the hue at 40 %, gone at 68 % (of the spill box's corner radius).
                float S = (R + 21.3) * 1.4142;
                float s = r / S;
                float4 warm = float4(float3(1.0, 0.965, 0.902) * 0.45, 0.45);
                float4 tint = float4(_Hue.rgb * _HueT, _HueT);
                float4 pool = s < 0.4 ? lerp(warm, tint, smoothstep(0.0, 1.0, s / 0.4))
                                      : tint * (1.0 - smoothstep(0.0, 1.0, saturate((s - 0.4) / 0.28)));
                pool *= 1.0 - smoothstep(R + 16.0, R + 26.0, r);
                OverPremul(acc, pool * _Opacity);

                // Imprint: a 1.2 mm line inside R at 0.4 with a soft glow.
                float d = r - R;
                float4 imprint = 0;
                Over(imprint, 1, OuterGlow(d, 0.2, 8.2, 0.0));
                Over(imprint, 1, 0.4 * Band(d + 0.6, 0.6, aa));
                OverPremul(acc, imprint * _Imprint);

                return half4(saturate(acc) * _Fade);
            }
            ENDHLSL
        }
    }
}
