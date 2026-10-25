// The model's data spine: a thin beam of light down the middle of the layer stack, with dashes flowing from input to
// output and a bright packet (_Packet, 0 = top .. 1 = bottom) when a metrics tick runs through the network. Drawn on
// a unit cube scaled to (width, _Length, width) mm. Additive over premultiplied alpha.
Shader "Forge/ML/Beam"
{
    Properties
    {
        _Color ("Color", Color) = (0.55, 0.75, 1, 1)
        _Length ("Length (mm)", Float) = 200
        _Packet ("Packet position (0 top, 1 bottom, <0 off)", Float) = -1
        _Fade ("Fade", Range(0, 1)) = 1
    }
    SubShader
    {
        Tags { "RenderType" = "Transparent" "Queue" = "Transparent+19" "RenderPipeline" = "UniversalPipeline" "IgnoreProjector" = "True" }
        Pass
        {
            Blend One OneMinusSrcAlpha
            ZWrite Off
            Cull Back

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma multi_compile_instancing
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
            float4 _Color;
            float _Length, _Packet, _Fade;
            CBUFFER_END

            struct Attributes { float4 positionOS : POSITION; UNITY_VERTEX_INPUT_INSTANCE_ID };
            struct Varyings { float4 positionCS : SV_POSITION; float y : TEXCOORD0; UNITY_VERTEX_OUTPUT_STEREO };

            Varyings Vert(Attributes v)
            {
                Varyings o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                o.positionCS = TransformObjectToHClip(v.positionOS.xyz);
                o.y = 0.5 - v.positionOS.y;   // 0 at the top
                return o;
            }

            half4 Frag(Varyings i) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);
                float mm = i.y * _Length;
                float dash = frac(mm / 14.0 - _Time.y * 1.6);
                dash = smoothstep(0.0, 0.25, dash) * (1.0 - smoothstep(0.45, 0.7, dash));
                float ends = smoothstep(0.0, 10.0, mm) * smoothstep(0.0, 10.0, _Length - mm);
                float packet = _Packet < 0.0 ? 0.0 : exp(-pow((mm - _Packet * _Length) / 9.0, 2.0));
                float a = saturate((0.22 + 0.45 * dash) * ends + packet);
                float3 c = lerp(_Color.rgb, 1.0, 0.35 + 0.5 * packet);
                return half4(c * a, a) * _Fade;
            }
            ENDHLSL
        }
    }
}
