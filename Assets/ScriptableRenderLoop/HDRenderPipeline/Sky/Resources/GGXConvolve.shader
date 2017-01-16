Shader "Hidden/HDRenderPipeline/GGXConvolve"
{
    SubShader
    {
        Pass
        {
            ZWrite Off
            ZTest LEqual
            Blend One Zero

            HLSLPROGRAM
            #pragma target 5.0
            #pragma only_renderers d3d11 ps4 // TEMP: unitl we go futher in dev

            #pragma multi_compile _ USE_MIS

            #pragma vertex Vert
            #pragma fragment Frag

            #include "Common.hlsl"
            #include "ImageBasedLighting.hlsl"
            #include "../SkyManager.cs.hlsl"

            struct Attributes
            {
                float3 positionCS : POSITION;
                float3 eyeVector : NORMAL;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 eyeVector : TEXCOORD0;
            };

            Varyings Vert(Attributes input)
            {
                Varyings output;
                output.positionCS = float4(input.positionCS.xy, UNITY_RAW_FAR_CLIP_VALUE, 1.0);
                output.eyeVector = input.eyeVector;
                return output;
            }

            TEXTURECUBE(_MainTex);
            SAMPLERCUBE(sampler_MainTex);

            TEXTURE2D(_IblGgxSamples);

            #ifdef USE_MIS
                TEXTURE2D(_MarginalRowDensities);
                TEXTURE2D(_ConditionalDensities);
            #endif

            float _Level;
            float _MaxLevel;
            float _InvOmegaP;

            half4 Frag(Varyings input) : SV_Target
            {
                // Vector interpolation is not magnitude-preserving.
                float3 N = normalize(input.eyeVector);
                // Remove view-dependency from GGX, effectively making the BSDF isotropic.
                float3 V = N;

                float perceptualRoughness = MipmapLevelToPerceptualRoughness(_Level);
                float roughness   = PerceptualRoughnessToRoughness(perceptualRoughness);
                uint  sampleCount = GetIBLRuntimeFilterSampleCount(_Level);

            #ifdef USE_MIS
                float4 val = IntegrateLD_MIS(TEXTURECUBE_PARAM(_MainTex, sampler_MainTex),
                                             _MarginalRowDensities, _ConditionalDensities,
                                             V, N,
                                             roughness,
                                             _InvOmegaP,
                                             LIGHTSAMPLINGPARAMETERS_TEXTURE_WIDTH,
                                             LIGHTSAMPLINGPARAMETERS_TEXTURE_HEIGHT,
                                             1024,
                                             false);
            #else
                float4 val = IntegrateLD(TEXTURECUBE_PARAM(_MainTex, sampler_MainTex),
                                         _IblGgxSamples,
                                         V, N,
                                         roughness,
                                         _Level - 1,
                                         _MaxLevel,
                                         _InvOmegaP,
                                         sampleCount, // Must be a Fibonacci number
                                         true,
                                         true);
            #endif

                return val;
            }
            ENDHLSL
        }
    }
}
