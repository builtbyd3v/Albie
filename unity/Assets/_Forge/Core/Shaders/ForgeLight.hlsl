#ifndef FORGE_LIGHT_INCLUDED
#define FORGE_LIGHT_INCLUDED

// Shared pieces of the Forge light shaders (NeonFrame, NeonRing, LightPool).
// Everything is premultiplied alpha (Blend One OneMinusSrcAlpha) so glows show over passthrough: the XR camera clears
// to transparent and passthrough appears wherever alpha is low. Distances are in mm (1 canvas unit = 1 mm).

#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

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
    float3 cameraOS : TEXCOORD1;   // the eye in the quad's object space (view-dependent effects)
    UNITY_VERTEX_OUTPUT_STEREO
};

Varyings ForgeVert(Attributes v)
{
    Varyings o;
    UNITY_SETUP_INSTANCE_ID(v);
    UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
    o.positionCS = TransformObjectToHClip(v.positionOS.xyz);
    o.uv = v.uv;
    o.color = v.color;
    o.cameraOS = TransformWorldToObject(GetCameraPositionWS());
    return o;
}

// Draw (color, alpha) on top of a premultiplied accumulator.
void Over(inout float4 acc, float3 color, float alpha)
{
    acc.rgb = color * alpha + acc.rgb * (1.0 - alpha);
    acc.a = alpha + acc.a * (1.0 - alpha);
}

void OverPremul(inout float4 acc, float4 premul)
{
    acc = premul + acc * (1.0 - premul.a);
}

// CSS box-shadow look: a shape blurred by `blur` (mm) and grown by `spread`, seen d mm outside the shape's edge.
// (A Gaussian edge, via the logistic approximation of the normal CDF.)
float OuterGlow(float d, float alpha, float blur, float spread)
{
    return d > 0.0 ? alpha / (1.0 + exp(3.404 * (d - spread) / blur)) : 0.0;
}

// Inset box-shadow: glow just inside the edge (d < 0 inside), fading inward.
float InsetGlow(float d, float alpha, float blur)
{
    return d < 0.0 ? alpha / (1.0 + exp(-3.404 * d / blur)) : 0.0;
}

// A crisp line |d| < halfWidth, antialiased over aa.
float Band(float d, float halfWidth, float aa)
{
    return 1.0 - smoothstep(halfWidth - aa, halfWidth + aa, abs(d));
}

// Signed distance to a rounded rectangle with squircle corners (CSS corner-shape: squircle, superellipse n = 4),
// corrected by the gradient so lines keep their width around the corners.
float SquircleBox(float2 p, float2 halfSize, float radius)
{
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

#endif
