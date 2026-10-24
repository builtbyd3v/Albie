using System.Reflection;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

/// <summary>
/// Fixes built-in (CG) shaders in single-pass XR under URP 17.6: TextMeshPro, uGUI, Forge/PanelSurface, the Interaction
/// SDK cursor and every module's UI. They read their stereo matrices from the engine's legacy UnityStereoGlobals
/// constants, which URP's camera setup leaves holding the mono camera (URP's own shaders read a separate buffer that it
/// fills correctly). Unfixed, they draw into the left eye only, from the middle of the head: text and buttons sit off
/// from the light quads and models, drift as the head moves, and are missing from the right eye.
/// For each single-pass XR camera this binds a constant buffer named UnityStereoGlobals with URP's per-eye matrices,
/// right after the camera setup; a globally bound buffer of that name takes the engine's place. Installs itself in
/// every scene; desktop and multi-pass cameras are untouched.
/// </summary>
public static class ForgeXRStereoFix
{
    public static bool Enabled = true;

    // UnityStereoGlobals as UnityShaderVariables.cginc declares it (float3 camera positions padded to float4).
    struct StereoGlobals
    {
        public Matrix4x4 p0, p1, v0, v1, invV0, invV1, vp0, vp1;
        public Matrix4x4 cameraP0, cameraP1, cameraInvP0, cameraInvP1;
        public Matrix4x4 worldToCamera0, worldToCamera1, cameraToWorld0, cameraToWorld1;
        public Vector4 cameraPos0, cameraPos1, scaleOffset0, scaleOffset1;
    }

    static readonly int StereoGlobalsId = Shader.PropertyToID("UnityStereoGlobals");
    static RestorePass pass;
    static StereoGlobals globals;
    // URP's per-eye values for this frame (XRBuiltinShaderConstants keeps them in private arrays).
    static Matrix4x4[] p, v, invV, vp, cameraP, cameraInvP;
    static Vector4[] cameraPos;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Install()
    {
        pass ??= new RestorePass();
        RenderPipelineManager.beginCameraRendering -= Enqueue;
        RenderPipelineManager.beginCameraRendering += Enqueue;
    }

    static void Enqueue(ScriptableRenderContext context, Camera camera)
    {
        if (!Enabled || !camera.stereoEnabled) return;
        var data = camera.GetUniversalAdditionalCameraData();
        if (data != null && data.scriptableRenderer != null) data.scriptableRenderer.EnqueuePass(pass);
    }

    static bool FindUrpArrays()
    {
        T Field<T>(string name) where T : class =>
            typeof(XRBuiltinShaderConstants).GetField(name, BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null) as T;
        p = Field<Matrix4x4[]>("s_projMatrix");
        v = Field<Matrix4x4[]>("s_viewMatrix");
        invV = Field<Matrix4x4[]>("s_invViewMatrix");
        vp = Field<Matrix4x4[]>("s_viewProjMatrix");
        cameraP = Field<Matrix4x4[]>("s_cameraProjMatrix");
        cameraInvP = Field<Matrix4x4[]>("s_invCameraProjMatrix");
        cameraPos = Field<Vector4[]>("s_worldSpaceCameraPos");
        if (p != null && v != null && invV != null && vp != null && cameraP != null && cameraInvP != null && cameraPos != null) return true;
        Debug.LogWarning("[ForgeXRStereoFix] This URP version has no per-eye arrays where expected; UI may be off in XR.");
        Enabled = false;
        return false;
    }

    static void Restore(UnsafeCommandBuffer cmd)
    {
        if (p == null && !FindUrpArrays()) return;
        var flipZ = Matrix4x4.Scale(new Vector3(1f, 1f, -1f));  // unity_WorldToCamera's handedness, as the engine sets it
        globals.p0 = p[0]; globals.p1 = p[1];
        globals.v0 = v[0]; globals.v1 = v[1];
        globals.invV0 = invV[0]; globals.invV1 = invV[1];
        globals.vp0 = vp[0]; globals.vp1 = vp[1];
        globals.cameraP0 = cameraP[0]; globals.cameraP1 = cameraP[1];
        globals.cameraInvP0 = cameraInvP[0]; globals.cameraInvP1 = cameraInvP[1];
        globals.worldToCamera0 = flipZ * v[0]; globals.worldToCamera1 = flipZ * v[1];
        globals.cameraToWorld0 = globals.worldToCamera0.inverse; globals.cameraToWorld1 = globals.worldToCamera1.inverse;
        globals.cameraPos0 = cameraPos[0]; globals.cameraPos1 = cameraPos[1];
        globals.scaleOffset0 = globals.scaleOffset1 = new Vector4(1f, 1f, 0f, 0f);
        ConstantBuffer.PushGlobal(CommandBufferHelpers.GetNativeCommandBuffer(cmd), globals, StereoGlobalsId);
    }

    // After the shadows: URP has set the camera up for the last time, and nothing has drawn into the eyes yet.
    class RestorePass : ScriptableRenderPass
    {
        class PassData { }

        public RestorePass() => renderPassEvent = RenderPassEvent.AfterRenderingShadows;

        public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
        {
            var xr = frameData.Get<UniversalCameraData>().xr;
            if (!xr.enabled || !xr.singlePassEnabled) return;
            using var builder = renderGraph.AddUnsafePass<PassData>("Forge XR stereo constants", out _);
            builder.AllowPassCulling(false);
            builder.AllowGlobalStateModification(true);
            builder.SetRenderFunc(static (PassData _, UnsafeGraphContext ctx) => Restore(ctx.cmd));
        }
    }
}
