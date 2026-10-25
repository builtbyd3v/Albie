// One model layer as a slab of edge-lit glass: a dark tinted top face with a faint cell grid (the layer's feature
// map), a bright rim on the sides as if lit from inside, a thin white-hot edge line and a sheen that slides with the
// eye. Drawn on a unit cube scaled to _Size (mm). Premultiplied alpha so it reads over passthrough and over panels.
Shader "Forge/ML/Slab"
{
    Properties
    {
        _Color ("Type color", Color) = (0.3, 0.55, 1, 1)
        _Size ("Size (mm)", Vector) = (120, 8, 90, 0)
        _Grid ("Top grid cells (x, z)", Vector) = (8, 8, 0, 0)
        _Hover ("Hover", Range(0, 1)) = 0
        _Pulse ("Pulse", Range(0, 1)) = 0
        _Fade ("Fade", Range(0, 1)) = 1
    }
    SubShader
    {
        Tags { "RenderType" = "Transparent" "Queue" = "Transparent+20" "RenderPipeline" = "UniversalPipeline" "IgnoreProjector" = "True" }
        Pass
        {
            Blend One OneMinusSrcAlpha
            ZWrite On
            Cull Back

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma multi_compile_instancing
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
            float4 _Color, _Size, _Grid;
            float _Hover, _Pulse, _Fade;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionOS : TEXCOORD0;
                float3 normalOS : TEXCOORD1;
                float3 cameraOS : TEXCOORD2;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings Vert(Attributes v)
            {
                Varyings o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                o.positionCS = TransformObjectToHClip(v.positionOS.xyz);
                o.positionOS = v.positionOS.xyz;
                o.normalOS = v.normalOS;
                o.cameraOS = TransformWorldToObject(GetCameraPositionWS());
                return o;
            }

            void Over(inout float4 acc, float3 color, float alpha)
            {
                acc.rgb = color * alpha + acc.rgb * (1.0 - alpha);
                acc.a = alpha + acc.a * (1.0 - alpha);
            }

            // Antialiased line of half width w (mm) at distance d (mm).
            float Line(float d, float w)
            {
                float aa = max(fwidth(d), 1e-3);
                return 1.0 - smoothstep(w - aa, w + aa, d);
            }

            half4 Frag(Varyings i) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);
                float3 h = _Size.xyz * 0.5;
                float3 p = i.positionOS * _Size.xyz;
                float3 a = max(h - abs(p), 0.0);   // mm to each face plane
                float3 n = i.normalOS;
                float3 hue = _Color.rgb;
                float3 hot = lerp(hue, 1.0, 0.7);
                float lit = 1.0 + 0.55 * _Hover + 1.1 * _Pulse;
                float4 acc = 0;

                if (n.y > 0.5)
                {
                    // Top: tinted smoked glass, the feature-map grid, a glow along the rim and the edge line.
                    float d = min(a.x, a.z);
                    float centre = saturate(1.0 - length(p.xz / h.xz) * 0.7);
                    Over(acc, lerp(float3(0.008, 0.010, 0.016), hue * 0.16, 0.6 + 0.4 * centre), 0.86);

                    float2 cell = (p.xz + h.xz) / (2.0 * h.xz) * _Grid.xy;
                    float2 toLine = abs(frac(cell + 0.5) - 0.5) / max(fwidth(cell), 1e-4);
                    float grid = 1.0 - saturate(min(toLine.x, toLine.y) - 0.25);
                    grid *= step(0.5, d);   // not over the edge line
                    Over(acc, hue, grid * (0.16 + 0.12 * _Hover + 0.3 * _Pulse));

                    Over(acc, hue, exp(-d / 5.0) * 0.45 * lit);
                    Over(acc, hot, Line(d, 0.55) * saturate(0.75 * lit));

                    // Sheen: a soft diagonal band that slides opposite the eye.
                    float2 eye = normalize(i.cameraOS.xz + 1e-4);
                    float g = dot(p.xz / h.xz, float2(0.707, 0.707)) * 0.5;
                    float sheen = saturate(1.0 - abs(g + 0.35 * dot(eye, float2(0.707, 0.707))) / 0.22);
                    Over(acc, 1.0, sheen * (0.05 + 0.06 * _Hover));
                }
                else if (n.y < -0.5)
                {
                    Over(acc, hue * 0.08, 0.9);
                    Over(acc, hue, Line(min(a.x, a.z), 0.5) * 0.4);
                }
                else
                {
                    // Sides: the rim, lit from inside, brightest at the top edge, the vertical corners catching light.
                    float t = saturate((p.y + h.y) / max(2.0 * h.y, 1e-3));
                    Over(acc, hue * (0.35 + 0.65 * t) * lit, 0.94);
                    float corner = abs(n.x) > 0.5 ? a.z : a.x;
                    Over(acc, hot, Line(corner, 0.45) * 0.6);
                    Over(acc, hot, Line(a.y, 0.5) * step(0.0, p.y) * saturate(0.8 * lit));
                }

                return half4(saturate(acc) * _Fade);
            }
            ENDHLSL
        }
    }
}
