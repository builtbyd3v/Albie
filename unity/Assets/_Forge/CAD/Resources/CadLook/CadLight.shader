// Light the CAD viewer draws at runtime (CadLines): leader and dimension lines, marker beads, the flight trail, the
// parachute, prop discs. Every line is a quad facing the eye with uv.x fixed at 0.5 and uv.y running across it, and a
// dot is a quad with uv across both axes, so one radial profile draws both: a crisp white-hot core and a soft glow in
// the vertex colour. A disc (uv in 2..3) is a flat pool instead: faint inside, brightening to a crisp rim.
// Premultiplied alpha (Blend One OneMinusSrcAlpha) like the Forge light shaders, so it shows over passthrough.
// _ZTest: LessEqual for things in the scene (the trail, the parachute), Always for annotations drawn over the model
// (markers, leaders, dimensions). Vertex colours arrive linear (CadLines converts them).
// In Resources so player builds include it (CadLook loads it by path).
Shader "Forge/CadLight"
{
    Properties
    {
        _Core ("Core (share of the half width)", Range(0, 1)) = 0.32
        _Glow ("Glow", Range(0, 2)) = 0.55
        [Enum(UnityEngine.Rendering.CompareFunction)] _ZTest ("ZTest", Float) = 4
    }
    SubShader
    {
        Tags { "RenderType" = "Transparent" "Queue" = "Transparent+20" "RenderPipeline" = "UniversalPipeline" "IgnoreProjector" = "True" "PreviewType" = "Plane" }
        Pass
        {
            Blend One OneMinusSrcAlpha
            ZWrite Off
            ZTest [_ZTest]
            Cull Off

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma multi_compile_instancing
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
            float _Core, _Glow;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv : TEXCOORD0;
                float4 color : COLOR;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
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
                o.color = v.color;
                return o;
            }

            half4 Frag(Varyings i) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);
                float disc = step(1.5, i.uv.x);                  // uv 2..3: a flat disc
                float2 p = i.uv - 0.5 - 2.0 * disc;
                float r = length(p) * 2.0;                       // 0 on the centre line (or centre), 1 at the edge
                float aa = max(fwidth(r), 1e-3);
                float core = 1.0 - smoothstep(_Core - aa, _Core + aa, r);
                float glow = saturate(1.0 - r);
                glow = glow * glow * _Glow;
                float lineAlpha = saturate(core + glow * (1.0 - core));
                float rim = 1.0 - smoothstep(1.0 - 2.0 * aa, 1.0, r);
                float r2 = r * r;
                float discAlpha = rim * (0.2 + 0.8 * r2 * r2);   // faint inside, brightening to the rim
                float a = lerp(lineAlpha, discAlpha, disc) * i.color.a;
                float3 hot = lerp(i.color.rgb, float3(1.0, 1.0, 1.0), core * 0.55);  // white-hot core, colour in the glow
                float3 rgb = lerp(hot, i.color.rgb, disc);
                return half4(rgb * a, a);
            }
            ENDHLSL
        }
    }
    Fallback Off
}
