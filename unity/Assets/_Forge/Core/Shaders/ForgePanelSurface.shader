// UI surfaces for ForgeSurface graphics, drawn as an SDF so corners stay crisp at any size and distance.
// Style 0, graphite panel: rounded window with a top-to-bottom graphite gradient, a 1 mm rim light along the top edge,
// a faint inner outline, the header hairline and a soft drop shadow. Style 1, fill: a flat rounded shape in the
// graphic's color with an optional glow in the same color (pills, handles).
// The graphic sends its shape per vertex: uv0 = position from the center (mm), uv1 = (half width, half height,
// corner, header), uv2 = (style, glow, shadow, outline). Premultiplied alpha, so it composites over passthrough.
Shader "Forge/PanelSurface"
{
    Properties
    {
        [PerRendererData] _MainTex ("Texture", 2D) = "white" {}
        _Top ("Panel top", Color) = (0.129, 0.141, 0.165, 0.96)
        _Bottom ("Panel bottom", Color) = (0.075, 0.082, 0.098, 0.96)

        _StencilComp ("Stencil Comparison", Float) = 8
        _Stencil ("Stencil ID", Float) = 0
        _StencilOp ("Stencil Operation", Float) = 0
        _StencilWriteMask ("Stencil Write Mask", Float) = 255
        _StencilReadMask ("Stencil Read Mask", Float) = 255
        _ColorMask ("Color Mask", Float) = 15
    }
    SubShader
    {
        Tags { "Queue" = "Transparent" "IgnoreProjector" = "True" "RenderType" = "Transparent" "PreviewType" = "Plane" "CanUseSpriteAtlas" = "True" }
        Stencil
        {
            Ref [_Stencil]
            Comp [_StencilComp]
            Pass [_StencilOp]
            ReadMask [_StencilReadMask]
            WriteMask [_StencilWriteMask]
        }
        Cull Off
        Lighting Off
        ZWrite Off
        ZTest [unity_GUIZTestMode]
        Blend One OneMinusSrcAlpha
        ColorMask [_ColorMask]

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0
            #pragma multi_compile_local _ UNITY_UI_CLIP_RECT
            #include "UnityCG.cginc"
            #include "UnityUI.cginc"

            struct appdata
            {
                float4 vertex : POSITION;
                float4 color : COLOR;
                float2 p : TEXCOORD0;
                float4 shape : TEXCOORD1;
                float4 style : TEXCOORD2;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct v2f
            {
                float4 vertex : SV_POSITION;
                float4 color : COLOR;
                float2 p : TEXCOORD0;
                float4 shape : TEXCOORD1;
                float4 style : TEXCOORD2;
                float4 worldPosition : TEXCOORD3;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            float4 _ClipRect;
            float4 _Top, _Bottom;

            v2f vert(appdata v)
            {
                v2f o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                o.worldPosition = v.vertex;
                o.vertex = UnityObjectToClipPos(v.vertex);
                o.color = v.color;
                o.p = v.p;
                o.shape = v.shape;
                o.style = v.style;
                return o;
            }

            void Over(inout float4 acc, float3 color, float alpha)
            {
                acc.rgb = color * alpha + acc.rgb * (1.0 - alpha);
                acc.a = alpha + acc.a * (1.0 - alpha);
            }

            float Band(float d, float halfWidth, float aa)
            {
                return 1.0 - smoothstep(halfWidth - aa, halfWidth + aa, abs(d));
            }

            // Rounded rectangle with squircle corners, distance corrected around the corners (same as ForgeLight.hlsl).
            float SquircleBox(float2 p, float2 halfSize, float radius)
            {
                radius = min(radius, min(halfSize.x, halfSize.y));
                float2 q = abs(p) - halfSize + radius;
                if (q.x > 0.0 && q.y > 0.0)
                {
                    float2 q2 = q * q;
                    float n = sqrt(sqrt(q2.x * q2.x + q2.y * q2.y));
                    float2 g = float2(q2.x * q.x, q2.y * q.y) / max(n * n * n, 1e-5);
                    return (n - radius) / max(length(g), 1e-3);
                }
                return max(q.x, q.y) - radius;
            }

            float4 frag(v2f i) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);
                float2 p = i.p;
                float2 halfSize = i.shape.xy;
                float corner = i.shape.z, header = i.shape.w;
                float d = SquircleBox(p, halfSize, corner);
                float aa = max(fwidth(d), 1e-3);
                float inside = 1.0 - smoothstep(-aa, aa, d);
                float4 acc = 0;

                if (i.style.x < 0.5)
                {
                    // Soft shadow under the window: 16 mm down, 40 mm blur.
                    float ds = SquircleBox(p + float2(0.0, 16.0), halfSize - 6.0, corner);
                    Over(acc, 0, 0.35 * i.style.z / (1.0 + exp(3.404 * ds / 40.0)));
                    // Graphite, lighter at the top.
                    float t = saturate((halfSize.y - p.y) / (2.0 * halfSize.y));
                    float4 fill = lerp(_Top, _Bottom, t);
                    Over(acc, fill.rgb, fill.a * inside);
                    // Faint inner outline, and a rim light along the top edge fading down into the corners.
                    float rim = Band(d + 0.5, 0.5, aa);
                    Over(acc, 1, 0.07 * rim);
                    Over(acc, 1, 0.16 * rim * smoothstep(halfSize.y - corner, halfSize.y - 2.0, p.y));
                    // Header hairline.
                    if (header > 0.0)
                    {
                        float hy = halfSize.y - header;
                        Over(acc, 1, 0.07 * Band(p.y - hy, 0.5, max(fwidth(p.y), 1e-3)) * step(d, -0.5));
                    }
                    acc *= i.color.a;
                }
                else
                {
                    // Flat fill with a glow in its own color (glow strength in style.y, scaled by the color's alpha).
                    float glow = d > 0.0 ? i.style.y * i.color.a / (1.0 + exp(3.404 * d / 8.0)) * 2.0 : 0.0;
                    Over(acc, i.color.rgb, saturate(glow));
                    Over(acc, i.color.rgb, i.color.a * inside);
                    Over(acc, i.color.rgb, i.style.w * Band(d + 0.5, 0.5, aa));
                }

                #ifdef UNITY_UI_CLIP_RECT
                acc *= UnityGet2DClipping(i.worldPosition.xy, _ClipRect);
                #endif
                return acc;
            }
            ENDCG
        }
    }
}
