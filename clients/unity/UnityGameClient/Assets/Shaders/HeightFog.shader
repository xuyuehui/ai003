Shader "AI003/HeightFog"
{
    SubShader
    {
        Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" }
        ZWrite Off
        Cull Off
        ZTest Always

        Pass
        {
            Name "HeightFog"

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"

            float4 _FogColor;
            float _Density;
            float _Falloff;
            float _StartDistance;
            float _BaseHeight;
            float _SunTint;

            half4 Frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
                float2 uv = input.texcoord;

                float3 color = SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, uv).rgb;
                float rawDepth = SampleSceneDepth(uv);

                // Sky pixels have no depth: leave them to the sky shader, which
                // already knits itself into the fog colour at the horizon.
                if (rawDepth <= 1e-6)
                {
                    return half4(color, 1.0);
                }

                float eyeDepth = LinearEyeDepth(rawDepth, _ZBufferParams);
                float3 worldPos = ComputeWorldSpacePosition(uv, rawDepth, UNITY_MATRIX_I_VP);

                float height = max(worldPos.y - _BaseHeight, 0.0);
                float distance = max(eyeDepth - _StartDistance, 0.0);
                float optical = _Density * exp(-height * _Falloff) * distance;
                float fog = 1.0 - exp(-optical);

                color = lerp(color, _FogColor.rgb, saturate(fog));
                return half4(color, 1.0);
            }
            ENDHLSL
        }
    }
    FallBack Off
}
