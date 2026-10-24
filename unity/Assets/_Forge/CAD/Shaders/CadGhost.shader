// See-through "what it was" surface for CadViewer: the fading ghost of changed parts, and dimmed
// parts while another one is focused. Unlit, alpha-blended, brighter at grazing angles so the
// silhouette reads. Alpha comes from _Color (driven per renderer with a MaterialPropertyBlock).
// Same style as cad-vision's shaders: plain UnityCG, no LightMode tag (URP draws it as SRPDefaultUnlit),
// single-pass-instanced stereo safe.
Shader "Forge/CadGhost"
{
    Properties
    {
        _Color ("Color", Color) = (0.55, 0.8, 1, 0.4)
        _Rim ("Rim", Range(0, 1)) = 0.6
    }

    SubShader
    {
        Tags { "Queue" = "Transparent" "RenderType" = "Transparent" "IgnoreProjector" = "True" }

        Pass
        {
            ZWrite Off
            Cull Back
            Blend SrcAlpha OneMinusSrcAlpha

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #include "UnityCG.cginc"

            fixed4 _Color;
            float _Rim;

            struct appdata
            {
                float4 vertex : POSITION;
                float3 normal : NORMAL;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
                float3 normal : TEXCOORD0;
                float3 view : TEXCOORD1;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            v2f vert(appdata v)
            {
                v2f o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_OUTPUT(v2f, o);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                o.pos = UnityObjectToClipPos(v.vertex);
                o.normal = UnityObjectToWorldNormal(v.normal);
                o.view = WorldSpaceViewDir(v.vertex);
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                float facing = abs(dot(normalize(i.normal), normalize(i.view)));
                fixed4 c = _Color;
                c.a *= lerp(1.0, 1.0 - facing, _Rim) + 0.2;
                return saturate(c);
            }
            ENDCG
        }
    }

    Fallback Off
}
