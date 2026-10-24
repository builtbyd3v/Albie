// The graphite chips behind the CAD viewer's labels (CadChips): a rounded rectangle with a soft dark shadow, so white
// text reads over a bright passthrough desk. One quad per chip, all in one mesh. uv0 is the pixel's position from the
// chip's centre and uv1 the shape (half width, half height, corner radius, shadow alpha), all in the label's mm.
// The vertex colour is the chip's (linear) with its opacity in alpha. Premultiplied alpha, like the Forge light.
// Drawn just before the text (Transparent-10), which is TextMeshPro on Transparent.
Shader "Forge/CadChip"
{
    Properties
    {
        _Shadow ("Shadow size (mm)", Float) = 7
    }
    SubShader
    {
        Tags { "RenderType" = "Transparent" "Queue" = "Transparent-10" "RenderPipeline" = "UniversalPipeline" "IgnoreProjector" = "True" "PreviewType" = "Plane" }
        Pass
        {
            Blend One OneMinusSrcAlpha
            ZWrite Off
            ZTest LEqual
            Cull Off

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma multi_compile_instancing
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
            float _Shadow;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv : TEXCOORD0;
                float4 shape : TEXCOORD1;
                float4 color : COLOR;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
                float4 shape : TEXCOORD1;
                float4 color : COLOR;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings Vert(Attributes v)
            {
                Varyings o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                o.positionCS = TransformObjectToHClip(v.positionOS.xyz);
                o.uv = v.uv;
                o.shape = v.shape;
                o.color = v.color;
                return o;
            }

            // Signed distance to a rounded box (mm): negative inside.
            float RoundedBox(float2 p, float2 halfSize, float radius)
            {
                float2 q = abs(p) - halfSize + radius;
                return length(max(q, 0.0)) + min(max(q.x, q.y), 0.0) - radius;
            }

            half4 Frag(Varyings i) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);
                float d = RoundedBox(i.uv, i.shape.xy, i.shape.z);
                float aa = max(fwidth(d), 1e-3);
                float fill = (1.0 - smoothstep(-aa, aa, d)) * i.color.a;
                float shadow = i.shape.w * (1.0 - smoothstep(0.0, max(_Shadow, 0.1), max(d, 0.0)));
                float a = saturate(fill + shadow * (1.0 - fill));
                return half4(i.color.rgb * fill, a);
            }
            ENDHLSL
        }
    }
    Fallback Off
}
