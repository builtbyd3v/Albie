// Dataset deck cards: one atlas image per instance (_UVRect picks its cell) on a rounded card with an antialiased
// edge and a thin light border. _Color tints and fades it (rgb = brightness, a = fade); premultiplied alpha, drawn
// after the panel so the deck floats over it. Instances draw in array order, so the caller sends them far to near.
Shader "Forge/ML/InstancedAtlas"
{
    Properties { _MainTex("Atlas", 2D) = "white" {} }
    SubShader
    {
        Tags { "RenderPipeline"="UniversalPipeline" "RenderType"="Transparent" "Queue"="Transparent+5" "IgnoreProjector"="True" }
        Pass
        {
            Blend One OneMinusSrcAlpha
            ZWrite Off
            Cull Off
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            TEXTURE2D(_MainTex); SAMPLER(sampler_MainTex);
            UNITY_INSTANCING_BUFFER_START(Props)
                UNITY_DEFINE_INSTANCED_PROP(float4, _UVRect)
                UNITY_DEFINE_INSTANCED_PROP(float4, _Color)
            UNITY_INSTANCING_BUFFER_END(Props)
            struct Attributes { float4 positionOS : POSITION; float2 uv : TEXCOORD0; UNITY_VERTEX_INPUT_INSTANCE_ID };
            // Stereo macros: in single-pass instanced XR the instance ID also picks the eye.
            struct Varyings { float4 positionCS : SV_POSITION; float2 uv : TEXCOORD0; float2 card : TEXCOORD1; half4 color : COLOR; UNITY_VERTEX_OUTPUT_STEREO };
            Varyings vert(Attributes v)
            {
                UNITY_SETUP_INSTANCE_ID(v);
                Varyings o; UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                float4 rect = UNITY_ACCESS_INSTANCED_PROP(Props, _UVRect);
                o.positionCS = TransformObjectToHClip(v.positionOS.xyz); o.uv = rect.xy + v.uv * rect.zw; o.card = v.uv;
                o.color = UNITY_ACCESS_INSTANCED_PROP(Props, _Color); return o;
            }
            half4 frag(Varyings i) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);
                // Rounded square in card space (0..1): -inside, 0 at the edge.
                float2 q = abs(i.card - 0.5);
                const float r = 0.07;
                float d = length(max(q - (0.5 - r), 0.0)) - r;
                float aa = max(fwidth(d), 1e-4);
                float shape = 1.0 - smoothstep(-aa, aa, d);
                float border = 1.0 - smoothstep(0.006 - aa, 0.006 + aa, -d);
                float3 c = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, i.uv).rgb * i.color.rgb;
                c = lerp(c, float3(0.9, 0.92, 0.96) * max(i.color.r, 0.35), border * 0.55);
                float a = shape * i.color.a;
                return half4(c * a, a);
            }
            ENDHLSL
        }
    }
}
