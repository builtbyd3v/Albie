// The results wall's dots, drawn by MLDotField in the panel's canvas (so popups layer over them): each dot a rounded
// tile showing its image washed with its status color (right, wrong, not predicted yet) and a rim in that color.
// uv0 = the image's cell in the atlas, uv1 = position across the tile (0..1). Premultiplied alpha.
Shader "Forge/ML/DotUI"
{
    Properties
    {
        [PerRendererData] _MainTex ("Atlas", 2D) = "white" {}
        _StencilComp ("Stencil Comparison", Float) = 8
        _Stencil ("Stencil ID", Float) = 0
        _StencilOp ("Stencil Operation", Float) = 0
        _StencilWriteMask ("Stencil Write Mask", Float) = 255
        _StencilReadMask ("Stencil Read Mask", Float) = 255
        _ColorMask ("Color Mask", Float) = 15
    }
    SubShader
    {
        Tags { "Queue" = "Transparent" "IgnoreProjector" = "True" "RenderType" = "Transparent" "PreviewType" = "Plane" }
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
                float2 uv : TEXCOORD0;
                float2 tile : TEXCOORD1;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct v2f
            {
                float4 vertex : SV_POSITION;
                float4 color : COLOR;
                float2 uv : TEXCOORD0;
                float2 tile : TEXCOORD1;
                float4 worldPosition : TEXCOORD2;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            sampler2D _MainTex;
            float4 _ClipRect;

            v2f vert(appdata v)
            {
                v2f o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                o.worldPosition = v.vertex;
                o.vertex = UnityObjectToClipPos(v.vertex);
                o.color = v.color;
                o.uv = v.uv;
                o.tile = v.tile;
                return o;
            }

            float4 frag(v2f i) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);
                float2 q = abs(i.tile - 0.5) * 2.0;
                const float r = 0.42;
                float d = length(max(q - (1.0 - r), 0.0)) - r;
                float aa = max(fwidth(d), 1e-3);
                float shape = 1.0 - smoothstep(-aa, aa, d);
                float rim = smoothstep(-0.30 - aa, -0.30 + aa, d);
                float3 image = tex2D(_MainTex, i.uv).rgb;
                float luma = dot(image, float3(0.3, 0.59, 0.11));
                float3 c = lerp(image * 0.85, i.color.rgb * (0.45 + 0.75 * luma), 0.55);
                c = lerp(c, i.color.rgb, rim * 0.9);
                float a = shape * i.color.a;
                #ifdef UNITY_UI_CLIP_RECT
                a *= UnityGet2DClipping(i.worldPosition.xy, _ClipRect);
                #endif
                return float4(c * a, a);
            }
            ENDCG
        }
    }
}
