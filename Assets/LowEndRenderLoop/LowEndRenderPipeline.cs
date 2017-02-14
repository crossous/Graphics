﻿using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

#region RenderPipelineInstance
public class LowEndRenderPipelineInstance : RenderPipeline
{
    private readonly LowEndRenderPipeline m_Asset;

    private const int MAX_CASCADES = 4;
    private int m_ShadowMapProperty;
    private int m_DepthBufferBits = 24;

    ShadowSettings m_ShadowSettings = ShadowSettings.Default;
    ShadowSliceData[] m_ShadowSlices = new ShadowSliceData[MAX_CASCADES];

    public LowEndRenderPipelineInstance(LowEndRenderPipeline asset)
    {
        m_Asset = asset;

        BuildShadowSettings();
        m_ShadowMapProperty = Shader.PropertyToID("_ShadowMap");
    }

    public override void Render(ScriptableRenderContext context, Camera[] cameras)
    {
        base.Render(context, cameras);

        foreach (Camera camera in cameras)
        {
            CullingParameters cullingParameters;
            camera.farClipPlane = 1000.0f;
            if (!CullResults.GetCullingParameters(camera, out cullingParameters))
                continue;

            cullingParameters.shadowDistance = QualitySettings.shadowDistance;
            CullResults cull = CullResults.Cull(ref cullingParameters, context);

            var cmd = new CommandBuffer() { name = "Clear" };
            cmd.ClearRenderTarget(true, false, Color.black);
            context.ExecuteCommandBuffer(cmd);
            cmd.Dispose();

            // Render Shadow Map
            bool shadowsRendered = RenderShadows(cull, context);

            // Draw Opaques with support to one directional shadow cascade
            // Setup camera matrices
            context.SetupCameraProperties(camera);

            // Setup light and shadow shader constants
            SetupLightShaderVariables(cull.visibleLights, context);
            if (shadowsRendered)
                SetupShadowShaderVariables(context, camera.nearClipPlane, cullingParameters.shadowDistance, m_ShadowSettings.directionalLightCascadeCount);

            // Rende Opaques
            var settings = new DrawRendererSettings(cull, camera, new ShaderPassName("LowEndForwardBase"));
            settings.sorting.flags = SortFlags.CommonOpaque;
            settings.inputFilter.SetQueuesOpaque();

            if (m_Asset.EnableLightmap)
                settings.rendererConfiguration = settings.rendererConfiguration | RendererConfiguration.PerObjectLightmaps;

            if (m_Asset.EnableAmbientProbe)
                settings.rendererConfiguration = settings.rendererConfiguration | RendererConfiguration.PerObjectLightProbe;

            context.DrawRenderers(ref settings);

            // TODO: Check skybox shader
            context.DrawSkybox(camera);

            // Rende Alpha blended
            settings.sorting.flags = SortFlags.CommonTransparent;
            settings.inputFilter.SetQueuesTransparent();
            context.DrawRenderers(ref settings);
        }

        context.Submit();
    }

    private void BuildShadowSettings()
    {
        m_ShadowSettings = ShadowSettings.Default;
        m_ShadowSettings.directionalLightCascadeCount = QualitySettings.shadowCascades;
        m_ShadowSettings.shadowAtlasWidth = m_Asset.ShadowAtlasResolution;
        m_ShadowSettings.shadowAtlasHeight = m_Asset.ShadowAtlasResolution;
        m_ShadowSettings.maxShadowDistance = QualitySettings.shadowDistance;

        switch (m_ShadowSettings.directionalLightCascadeCount)
        {
            case 1:
                m_ShadowSettings.directionalLightCascades = new Vector3(1.0f, 0.0f, 0.0f);
                break;

            case 2:
                m_ShadowSettings.directionalLightCascades = new Vector3(QualitySettings.shadowCascade2Split, 1.0f, 0.0f);
                break;

            case 4:
                m_ShadowSettings.directionalLightCascades = QualitySettings.shadowCascade4Split;
                break;

            default:
                Debug.LogError("Invalid Shadow Cascade Settings");
                m_ShadowSettings = ShadowSettings.Default;
                break;
        }
    }

    #region HelperMethods
    private void SetupLightShaderVariables(VisibleLight[] lights, ScriptableRenderContext context)
    {
        if (lights.Length <= 0)
            return;

        const int kMaxLights = 8;
        Vector4[] lightPositions = new Vector4[kMaxLights];
        Vector4[] lightColors = new Vector4[kMaxLights];
        Vector4[] lightAttenuations = new Vector4[kMaxLights];
        Vector4[] lightSpotDirections = new Vector4[kMaxLights];

        int pixelLightCount = Mathf.Min(lights.Length, QualitySettings.pixelLightCount);
        int vertexLightCount = (m_Asset.SupportsVertexLight) ? Mathf.Min(lights.Length - pixelLightCount, kMaxLights) : 0;
        int totalLightCount = pixelLightCount + vertexLightCount;

        for (int i = 0; i < totalLightCount; ++i)
        {
            VisibleLight currLight = lights[i];
            if (currLight.lightType == LightType.Directional)
            {
                Vector4 dir = -currLight.localToWorld.GetColumn(2);
                lightPositions[i] = new Vector4(dir.x, dir.y, dir.z, 0.0f);
            }
            else
            {
                Vector4 pos = currLight.localToWorld.GetColumn(3);
                lightPositions[i] = new Vector4(pos.x, pos.y, pos.z, 1.0f);
            }

            lightColors[i] = currLight.finalColor;

            float rangeSq = currLight.range * currLight.range;
            float quadAtten = (currLight.lightType == LightType.Directional) ? 0.0f : 25.0f / rangeSq;

            if (currLight.lightType == LightType.Spot)
            {
                Vector4 dir = currLight.localToWorld.GetColumn(2);
                lightSpotDirections[i] = new Vector4(-dir.x, -dir.y, -dir.z, 0.0f);

                float spotAngle = Mathf.Deg2Rad * currLight.spotAngle;
                float cosOuterAngle = Mathf.Cos(spotAngle * 0.5f);
                float cosInneAngle = Mathf.Cos(spotAngle * 0.25f);
                float angleRange = cosInneAngle - cosOuterAngle;
                lightAttenuations[i] = new Vector4(cosOuterAngle,
                    Mathf.Approximately(angleRange, 0.0f) ? 1.0f : angleRange, quadAtten, rangeSq);
            }
            else
            {
                lightSpotDirections[i] = new Vector4(0.0f, 0.0f, 1.0f, 0.0f);
                lightAttenuations[i] = new Vector4(-1.0f, 1.0f, quadAtten, rangeSq);
            }
        }

        CommandBuffer cmd = new CommandBuffer() { name = "SetupShadowShaderConstants" };
        cmd.SetGlobalVectorArray("globalLightPos", lightPositions);
        cmd.SetGlobalVectorArray("globalLightColor", lightColors);
        cmd.SetGlobalVectorArray("globalLightAtten", lightAttenuations);
        cmd.SetGlobalVectorArray("globalLightSpotDir", lightSpotDirections);
        cmd.SetGlobalVector("globalLightCount", new Vector4(pixelLightCount, totalLightCount, 0.0f, 0.0f));
        SetShadowKeywords(cmd);
        context.ExecuteCommandBuffer(cmd);
        cmd.Dispose();
    }

    private bool RenderShadows(CullResults cullResults, ScriptableRenderContext context)
    {
        int cascadeCount = m_ShadowSettings.directionalLightCascadeCount;

        VisibleLight[] lights = cullResults.visibleLights;
        int lightCount = lights.Length;

        int shadowResolution = 0;
        int lightIndex = -1;
        float shadowBias = 0.0f;
        for (int i = 0; i < lightCount; ++i)
        {
            if (lights[i].light.shadows != LightShadows.None && lights[i].lightType == LightType.Directional)
            {
                lightIndex = i;
                shadowResolution = GetMaxTileResolutionInAtlas(m_ShadowSettings.shadowAtlasWidth,
                    m_ShadowSettings.shadowAtlasHeight, cascadeCount);
                shadowBias = lights[i].light.shadowBias;
                break;
            }
        }

        if (lightIndex < 0)
            return false;

        Bounds bounds;
        if (!cullResults.GetShadowCasterBounds(lightIndex, out bounds))
            return false;

        var setRenderTargetCommandBuffer = new CommandBuffer();
        setRenderTargetCommandBuffer.name = "Render packed shadows";
        setRenderTargetCommandBuffer.GetTemporaryRT(m_ShadowMapProperty, m_ShadowSettings.shadowAtlasWidth, m_ShadowSettings.shadowAtlasHeight, m_DepthBufferBits, FilterMode.Bilinear, RenderTextureFormat.Depth, RenderTextureReadWrite.Linear);
        setRenderTargetCommandBuffer.SetRenderTarget(new RenderTargetIdentifier(m_ShadowMapProperty));
        setRenderTargetCommandBuffer.ClearRenderTarget(true, true, Color.green);
        context.ExecuteCommandBuffer(setRenderTargetCommandBuffer);
        setRenderTargetCommandBuffer.Dispose();

        float shadowNearPlane = QualitySettings.shadowNearPlaneOffset;
        Vector3 splitRatio = m_ShadowSettings.directionalLightCascades;
        for (int cascadeIdx = 0; cascadeIdx < cascadeCount; ++cascadeIdx)
        {
            Matrix4x4 view, proj;
            var settings = new DrawShadowsSettings(cullResults, lightIndex);
            bool needRendering = cullResults.ComputeDirectionalShadowMatricesAndCullingPrimitives(lightIndex, cascadeIdx, cascadeCount, splitRatio, shadowResolution, shadowNearPlane, out view, out proj, out settings.splitData);

            if (needRendering)
            {
                SetupShadowSliceTransform(cascadeIdx, shadowResolution, proj, view);
                RenderShadowSlice(ref context, cascadeIdx, proj, view, settings, shadowBias);
            }
        }

        return true;
    }

    private void SetupShadowSliceTransform(int cascadeIndex, int shadowResolution, Matrix4x4 proj, Matrix4x4 view)
    {
        // Assumes MAX_CASCADES = 4
        m_ShadowSlices[cascadeIndex].atlasX = (cascadeIndex % 2) * shadowResolution;
        m_ShadowSlices[cascadeIndex].atlasY = (cascadeIndex / 2) * shadowResolution;
        m_ShadowSlices[cascadeIndex].shadowResolution = shadowResolution;
        m_ShadowSlices[cascadeIndex].shadowTransform = Matrix4x4.identity;

        var matScaleBias = Matrix4x4.identity;
        matScaleBias.m00 = 0.5f;
        matScaleBias.m11 = 0.5f;
        matScaleBias.m22 = 0.5f;
        matScaleBias.m03 = 0.5f;
        matScaleBias.m23 = 0.5f;
        matScaleBias.m13 = 0.5f;

        // Later down the pipeline the proj matrix will be scaled to reverse-z in case of DX. 
        // We need account for that scale in the shadowTransform.
        if (SystemInfo.usesReversedZBuffer)
            matScaleBias.m22 = -0.5f;

        var matTile = Matrix4x4.identity;
        matTile.m00 = (float)m_ShadowSlices[cascadeIndex].shadowResolution / (float)m_ShadowSettings.shadowAtlasWidth;
        matTile.m11 = (float)m_ShadowSlices[cascadeIndex].shadowResolution / (float)m_ShadowSettings.shadowAtlasHeight;
        matTile.m03 = (float)m_ShadowSlices[cascadeIndex].atlasX / (float)m_ShadowSettings.shadowAtlasWidth;
        matTile.m13 = (float)m_ShadowSlices[cascadeIndex].atlasY / (float)m_ShadowSettings.shadowAtlasHeight;

        m_ShadowSlices[cascadeIndex].shadowTransform = matTile * matScaleBias * proj * view;
    }

    private void RenderShadowSlice(ref ScriptableRenderContext context, int cascadeIndex, Matrix4x4 proj, Matrix4x4 view, DrawShadowsSettings settings, float shadowBias)
    { 
        var buffer = new CommandBuffer() { name = "RenderShadowMap" };
        buffer.SetViewport(new Rect(m_ShadowSlices[cascadeIndex].atlasX, m_ShadowSlices[cascadeIndex].atlasY, m_ShadowSlices[cascadeIndex].shadowResolution, m_ShadowSlices[cascadeIndex].shadowResolution));
        buffer.SetViewProjectionMatrices(view, proj);
        buffer.SetGlobalVector("_ShadowBias", new Vector4(shadowBias, 0.0f, 0.0f, 0.0f));
        context.ExecuteCommandBuffer(buffer);
        buffer.Dispose();

        context.DrawShadows(ref settings);
    }

    private int GetMaxTileResolutionInAtlas(int atlasWidth, int atlasHeight, int tileCount)
    {
        int resolution = Mathf.Min(atlasWidth, atlasHeight);
        if (tileCount > Mathf.Log(resolution))
        {
            //Debug.LogError(String.Format("Cannot fit {0} tiles into current shadowmap atlas of size ({1}, {2}). ShadowMap Resolution set to zero.", tileCount, atlasWidth, atlasHeight));
            return 0;
        }

        int currentTileCount = atlasWidth / resolution * atlasHeight / resolution;
        while (currentTileCount < tileCount)
        {
            resolution = resolution >> 1;
            currentTileCount = atlasWidth / resolution * atlasHeight / resolution;
        }
        return resolution;
    }

    void SetupShadowShaderVariables(ScriptableRenderContext context, float shadowNear, float shadowFar, int cascadeCount)
    {
        float shadowResolution = m_ShadowSlices[0].shadowResolution;

        // PSSM distance settings
        float shadowFrustumDepth = shadowFar - shadowNear;
        Vector3 shadowSplitRatio = m_ShadowSettings.directionalLightCascades;

        // We set PSSMDistance to infinity for non active cascades so the comparison test always fails for unavailable cascades
        Vector4 PSSMDistances = new Vector4(
            shadowNear + shadowSplitRatio.x * shadowFrustumDepth,
            (shadowSplitRatio.y > 0.0f) ? shadowNear + shadowSplitRatio.y * shadowFrustumDepth : Mathf.Infinity,
            (shadowSplitRatio.z > 0.0f) ? shadowNear + shadowSplitRatio.z * shadowFrustumDepth : Mathf.Infinity,
            1.0f / shadowResolution);

        const int maxShadowCascades = 4;
        Matrix4x4[] shadowMatrices = new Matrix4x4[maxShadowCascades];
        for (int i = 0; i < cascadeCount; ++i)
            shadowMatrices[i] = (cascadeCount >= i) ? m_ShadowSlices[i].shadowTransform : Matrix4x4.identity;

        // TODO: shadow resolution per cascade in case cascades endup being supported.
        float invShadowResolution = 1.0f / shadowResolution;
        float[] pcfKernel = {-1.5f * invShadowResolution,  0.5f * invShadowResolution,
                              0.5f * invShadowResolution,  0.5f * invShadowResolution,
                             -1.5f * invShadowResolution, -0.5f * invShadowResolution,
                              0.5f * invShadowResolution, -0.5f * invShadowResolution };

        var setupShadow = new CommandBuffer() { name = "SetupShadowShaderConstants" };
        SetShadowKeywords(setupShadow);
        setupShadow.SetGlobalMatrixArray("_WorldToShadow", shadowMatrices);
        setupShadow.SetGlobalVector("_PSSMDistancesAndShadowResolution", PSSMDistances);
        setupShadow.SetGlobalFloatArray("_PCFKernel", pcfKernel);
        SetShadowKeywords(setupShadow);
        context.ExecuteCommandBuffer(setupShadow);
        setupShadow.Dispose();
    }

    void SetShadowKeywords(CommandBuffer cmd)
    {
        if (QualitySettings.shadows == ShadowQuality.Disable)
            cmd.DisableShaderKeyword("SHADOWS_DEPTH");
        else
            cmd.EnableShaderKeyword("SHADOWS_DEPTH");

        switch (m_Asset.CurrShadowFiltering)
        {
            case LowEndRenderPipeline.ShadowFiltering.PCF:
                cmd.EnableShaderKeyword("SHADOWS_FILTERING_PCF");
                break;
            default:
                cmd.DisableShaderKeyword("SHADOWS_FILTERING_PCF");
                break;
        }
    }
    #endregion
}
#endregion

public class LowEndRenderPipeline : RenderPipelineAsset
{
#region AssetAndPipelineCreation
#if UNITY_EDITOR
    [UnityEditor.MenuItem("Renderloop/Create Low End Pipeline")]
    static void CreateLowEndPipeline()
    {
        var instance = ScriptableObject.CreateInstance<LowEndRenderPipeline>();
        UnityEditor.AssetDatabase.CreateAsset(instance, "Assets/LowEndRenderLoop/LowEndPipeline.asset");
    }
#endif

    protected override IRenderPipeline InternalCreatePipeline()
    {
        return new LowEndRenderPipelineInstance(this);
    }
    #endregion

#region PipelineAssetSettings
    public enum ShadowFiltering
    {
        PCF = 0,
        NONE
    }
    public bool m_SupportsVertexLight = true;
    public bool m_EnableLightmaps = true;
    public bool m_EnableAmbientProbe = true;
    public int m_ShadowAtlasResolution = 1024;
    public ShadowFiltering m_ShadowFiltering = ShadowFiltering.NONE;
    

    public bool SupportsVertexLight { get { return m_SupportsVertexLight;} private set { m_SupportsVertexLight = value; } }

    public bool EnableLightmap { get { return m_EnableLightmaps;} private set { m_EnableLightmaps = value; } }

    public bool EnableAmbientProbe { get { return m_EnableAmbientProbe; } private set { m_EnableAmbientProbe = value; } }

    public ShadowFiltering CurrShadowFiltering { get { return m_ShadowFiltering; } private set { m_ShadowFiltering = value; } }

    public int ShadowAtlasResolution { get { return m_ShadowAtlasResolution; } private set { m_ShadowAtlasResolution = value; } }
#endregion
}