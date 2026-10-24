// The dock drawn as light on the desk, in one quad: a soft dark wash (so light reads on a bright real desk), one
// rounded-rectangle line of white light with glows, a faint sheen that slides opposite the head, the divider between
// modes and tools, and the hub status dot.
Shader "Forge/NeonFrame"
{
    Properties
    {
        _Size ("Quad size (mm)", Vector) = (675, 195, 0, 0)
        _Box ("Frame half size (mm)", Vector) = (297.5, 57.5, 0, 0)
        _Corner ("Corner radius (mm)", Float) = 28
        _Divider ("Divider (x, half length, on)", Vector) = (0, 23, 0, 0)
        _Hub ("Hub dot (x, y, radius, on)", Vector) = (0, 0, 3.25, 0)
        _HubColor ("Hub color", Color) = (1, 0.75, 0.3, 1)
        _HubGlow ("Hub glow", Color) = (1, 0.71, 0.18, 1)
        _Sheen ("Sheen", Float) = 0.07
        _Wash ("Wash", Float) = 1
        _Fade ("Fade", Range(0, 1)) = 1
    }
    SubShader
    {
        Tags { "RenderType" = "Transparent" "Queue" = "Transparent+10" "RenderPipeline" = "UniversalPipeline" "IgnoreProjector" = "True" "PreviewType" = "Plane" }
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
            float4 _Size, _Box, _Divider, _Hub, _HubColor, _HubGlow;
            float _Corner, _Sheen, _Wash, _Fade;
            CBUFFER_END

            half4 Frag(Varyings i) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);
                float2 p = (i.uv - 0.5) * _Size.xy;
                float d = SquircleBox(p, _Box.xy, _Corner);
                float aa = max(fwidth(d), 1e-3);
                float4 acc = 0;

                // Contrast wash: 0.30 in the middle, 0.18 at 60 %, gone at the edge, ~15 mm past the frame.
                float2 washHalf = _Box.xy + 15.0;
                float e = length(p / washHalf) * 0.7071;
                float wash = e < 0.6 ? lerp(0.30, 0.18, e / 0.6) : lerp(0.18, 0.0, saturate((e - 0.6) / 0.4));
                wash *= 1.0 - smoothstep(-6.0, 6.0, SquircleBox(p, washHalf, _Corner + 15.0));
                Over(acc, float3(0.039, 0.047, 0.078), wash * _Wash);

                // Sheen: a faint warm band, slanted 10 deg, sliding opposite the eye's sideways offset.
                float2 dir = float2(0.9848, -0.1736);
                float span = 2.0 * _Box.x * 0.9848 + 2.0 * _Box.y * 0.1736;
                float g = dot(p, dir) / span + 0.5;
                float eyeX = clamp(i.cameraOS.x * _Size.x / (2.0 * _Box.x), -0.5, 0.5);
                float sheen = saturate(1.0 - abs(g - (0.5 - 0.9 * eyeX)) / 0.25);
                Over(acc, float3(1.0, 0.973, 0.925), sheen * _Sheen * (1.0 - smoothstep(-aa, aa, d)));

                // Frame: one line, 1.5 mm pure white on the inside edge, glows at 5 / 15 / 28 mm and an inner glow.
                Over(acc, 1, OuterGlow(d, 0.14, 28.0, 0.0));
                Over(acc, 1, OuterGlow(d, 0.34, 15.0, 0.0));
                Over(acc, 1, OuterGlow(d, 0.7, 5.0, 0.0));
                Over(acc, 1, InsetGlow(d, 0.24, 11.5));
                Over(acc, 1, Band(d + 0.75, 0.75, aa));

                // Divider: a 1.2 mm line fading out at both ends, with a soft glow.
                if (_Divider.z > 0.5)
                {
                    float dx = abs(p.x - _Divider.x);
                    float along = saturate(1.0 - abs(p.y) / _Divider.y);
                    float4 divider = 0;
                    Over(divider, 1, OuterGlow(max(dx - 0.6, 0.001), 0.35, 4.9, 0.0) * smoothstep(0.0, 0.6, along));
                    Over(divider, 1, Band(dx, 0.6, max(fwidth(p.x), 1e-3)) * along);
                    OverPremul(acc, divider * 0.6);
                }

                // Hub dot.
                if (_Hub.w > 0.5)
                {
                    float dh = length(p - _Hub.xy) - _Hub.z;
                    float4 hub = 0;
                    Over(hub, _HubGlow.rgb, OuterGlow(dh, 0.5, 11.5, 1.6));
                    Over(hub, _HubGlow.rgb, OuterGlow(dh, 1.0, 4.9, 0.8));
                    Over(hub, _HubColor.rgb, 1.0 - smoothstep(-aa, aa, dh));
                    OverPremul(acc, hub);
                }

                return half4(saturate(acc) * _Fade);
            }
            ENDHLSL
        }
    }
}
