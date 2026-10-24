// Selection outline, step 2 of 2 (Built-in Render Pipeline, Quest-safe): one continuous rim
// around the whole selection's silhouette.
// Draws the back faces of the selected meshes pushed outward along smoothed normals by a
// constant on-screen width, only where the selection mask (stencil bit 2, CADSelectionMask)
// is NOT set, so the rim appears outside the combined silhouette only: no inner lines between
// the parts of a selected assembly, and no fill over the parts themselves.
// No z-fighting: vertices are also pushed slightly AWAY from the eye, so the rim is never
// coplanar with a touching neighbor's surface; anything nearer (in front) still hides it.
// If the eye buffer had no stencil, this degrades to a plain back-face rim, never a fill.
// Queue 2990: after the mask (2988) and every CAD layer, before UI canvases (3000).
// Single-pass-instanced stereo safe.
Shader "CADVision/SelectionOutline"
{
    Properties
    {
        _OutlineColor ("Outline Color", Color) = (0, 0.9, 1, 1)
        _OutlineWidth ("Outline Width (fraction of view height)", Range(0, 0.02)) = 0.004
        _DepthPush ("Depth push (fraction of view depth)", Range(0, 0.05)) = 0.01
    }

    SubShader
    {
        Tags { "Queue" = "Transparent-10" "RenderType" = "Transparent" "IgnoreProjector" = "True" }

        Pass
        {
            Name "Outline"
            Cull Front
            ZWrite Off
            ZTest LEqual
            Stencil
            {
                Ref 2
                ReadMask 2
                Comp NotEqual
            }

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #include "UnityCG.cginc"

            fixed4 _OutlineColor;
            float _OutlineWidth;
            float _DepthPush;

            struct appdata
            {
                float4 vertex : POSITION;
                float3 normal : NORMAL;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            v2f vert(appdata v)
            {
                v2f o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_OUTPUT(v2f, o);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);

                float3 viewPos = UnityObjectToViewPos(v.vertex.xyz);
                viewPos *= 1.0 + _DepthPush; // Away from the eye: never coplanar with neighbors.
                float4 clipPos = mul(UNITY_MATRIX_P, float4(viewPos, 1.0));

                // Normal direction in clip space (xy), independent of object scale.
                float3 viewNormal = mul((float3x3)UNITY_MATRIX_IT_MV, v.normal);
                float2 clipNormal = mul((float2x2)UNITY_MATRIX_P, viewNormal.xy);
                float len = length(clipNormal);
                if (len > 1e-5)
                    clipNormal /= len;
                // Multiply by w for a constant on-screen width at any distance; x2 = NDC range.
                clipPos.xy += clipNormal * _OutlineWidth * 2.0 * clipPos.w;
                o.pos = clipPos;
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                return _OutlineColor;
            }
            ENDCG
        }
    }

    Fallback Off
}
