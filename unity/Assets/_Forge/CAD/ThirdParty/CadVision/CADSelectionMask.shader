// Selection outline, step 1 of 2 (Built-in Render Pipeline, Quest-safe): marks every pixel
// covered by the selected geometry in stencil bit 2 (whole silhouette, regardless of what is in
// front), drawing no color and no depth. Queue 2988: after all CAD surfaces, edge overlays and
// see-through Wireframe / ghost surfaces, and before the outline rim (2990), so the mask for
// the whole selection is complete before any rim is drawn. Single-pass-instanced stereo safe.
Shader "CADVision/SelectionMask"
{
    SubShader
    {
        Tags { "Queue" = "Transparent-12" "RenderType" = "Transparent" "IgnoreProjector" = "True" }

        Pass
        {
            Name "Mask"
            Cull Off
            ZWrite Off
            ZTest Always
            ColorMask 0
            Stencil
            {
                Ref 2
                ReadMask 2
                WriteMask 2
                Comp Always
                Pass Replace
            }

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #include "UnityCG.cginc"

            struct appdata
            {
                float4 vertex : POSITION;
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
                o.pos = UnityObjectToClipPos(v.vertex);
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                return 0;
            }
            ENDCG
        }
    }

    Fallback Off
}
