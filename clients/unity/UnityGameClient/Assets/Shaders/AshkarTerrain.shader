Shader "AI003/AshkarTerrain"
{
    Properties
    {
        _Smoothness ("Smoothness", Range(0, 1)) = 0.18
        _RippleStrength ("Sand Ripple Strength", Range(0, 2)) = 0.35
        _Speckle ("Speckle", Range(0, 1)) = 0.25
        _Wrap ("Diffuse Wrap", Range(0, 1)) = 0.35
        _HeightFogColor ("Height Fog Colour", Color) = (0.9, 0.8, 0.65, 1)
        _HeightFogStrength ("Height Fog Strength", Range(0, 1)) = 0.0
        _HeightFogFalloff ("Height Fog Falloff", Float) = 0.012
        _HeightFogDistance ("Height Fog Distance", Float) = 2200
    }

    SubShader
    {
        Tags
        {
            "RenderType" = "Opaque"
            "RenderPipeline" = "UniversalPipeline"
            "Queue" = "Geometry"
        }

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE
            #pragma multi_compile _ _ADDITIONAL_LIGHTS
            #pragma multi_compile _ _ADDITIONAL_LIGHT_SHADOWS
            #pragma multi_compile _ _SHADOWS_SOFT
            #pragma multi_compile_fog

            #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DOTS.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float _Smoothness;
                float _RippleStrength;
                float _Speckle;
                float _Wrap;
                float4 _HeightFogColor;
                float _HeightFogStrength;
                float _HeightFogFalloff;
                float _HeightFogDistance;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
                float4 color : COLOR;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 normalWS : TEXCOORD0;
                float3 positionWS : TEXCOORD1;
                float4 color : COLOR;
                float fogFactor : TEXCOORD2;
            };

            Varyings vert(Attributes input)
            {
                Varyings output;
                VertexPositionInputs positions = GetVertexPositionInputs(input.positionOS.xyz);
                output.positionCS = positions.positionCS;
                output.positionWS = positions.positionWS;
                output.normalWS = TransformObjectToWorldNormal(input.normalOS);
                output.color = input.color;
                output.fogFactor = ComputeFogFactor(positions.positionCS.z);
                return output;
            }

            float Hash21(float2 p)
            {
                p = frac(p * float2(123.34, 456.21));
                p += dot(p, p + 45.32);
                return frac(p.x * p.y);
            }

            float ValueNoise(float2 p)
            {
                float2 i = floor(p);
                float2 f = frac(p);
                f = f * f * (3.0 - 2.0 * f);
                float a = Hash21(i);
                float b = Hash21(i + float2(1.0, 0.0));
                float c = Hash21(i + float2(0.0, 1.0));
                float d = Hash21(i + float2(1.0, 1.0));
                return lerp(lerp(a, b, f.x), lerp(c, d, f.x), f.y);
            }

            half4 frag(Varyings input) : SV_Target
            {
                float3 normalWS = normalize(input.normalWS);
                float2 p = input.positionWS.xz;
                float up = saturate(normalWS.y);
                float dist = distance(input.positionWS, _WorldSpaceCameraPos);

                // Wind ripples: one warped directional wave, fading with distance
                // so it never aliases into moire.
                float2 flow = float2(0.82, 0.57);
                float phase = dot(p, flow) * 1.7
                              + ValueNoise(p * 0.10) * 5.0
                              + ValueNoise(p * 0.55) * 1.2;
                float2 flow2 = float2(-0.48, 0.88);
                float phase2 = dot(p, flow2) * 3.9 + ValueNoise(p * 0.23) * 3.4;
                float fade = saturate(1.0 - dist / 170.0);
                float amp = _RippleStrength * up * up * fade;
                normalWS = normalize(normalWS
                    + float3(flow.x, 0.0, flow.y) * cos(phase) * amp
                    + float3(flow2.x, 0.0, flow2.y) * cos(phase2) * amp * 0.5);

                float4 shadowCoord = TransformWorldToShadowCoord(input.positionWS);
                Light mainLight = GetMainLight(shadowCoord);

                float ndl = saturate(dot(normalWS, mainLight.direction));
                float diff = saturate((ndl + _Wrap) / (1.0 + _Wrap));

                // Grain: two octaves of value noise keep big flat dunes alive.
                float grain = ValueNoise(p * 0.85) * 0.62 + ValueNoise(p * 3.7) * 0.38;
                float3 albedo = input.color.rgb * ((1.0 - _Speckle * 0.5) + grain * _Speckle);

                float3 ambient = SampleSH(normalWS);
                float3 color = albedo * (ambient + mainLight.color * diff * mainLight.shadowAttenuation);

                // Grazing specular: the only thing that sells "sand" at a distance.
                float3 viewDir = normalize(GetWorldSpaceViewDir(input.positionWS));
                float3 halfDir = normalize(mainLight.direction + viewDir);
                float spec = pow(saturate(dot(normalWS, halfDir)), 48.0) * _Smoothness;
                color += mainLight.color * spec * mainLight.shadowAttenuation * 0.8;

                // Additional lights (the second sun) — URP only.
                uint count = GetAdditionalLightsCount();
                for (uint i = 0u; i < count; ++i)
                {
                    Light light = GetAdditionalLight(i, input.positionWS);
                    float ndl2 = saturate(dot(normalWS, light.direction));
                    float diff2 = saturate((ndl2 + _Wrap) / (1.0 + _Wrap));
                    color += albedo * light.color * diff2 * light.distanceAttenuation * 0.65;
                }

                color = MixFog(color, input.fogFactor);

                // Height fog: haze pools in the low ground, so distant dunes read
                // as layers instead of a flat silhouette.
                float lowHaze = exp(-max(input.positionWS.y, 0.0) * _HeightFogFalloff)
                                * saturate(dist / _HeightFogDistance)
                                * _HeightFogStrength;
                color = lerp(color, _HeightFogColor.rgb, saturate(lowHaze));

                return half4(color, 1.0);
            }
            ENDHLSL
        }

        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode" = "ShadowCaster" }

            ZWrite On
            ZTest LEqual
            ColorMask 0

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"

            float3 _LightDirection;

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
            };

            Varyings vert(Attributes input)
            {
                Varyings output;
                float3 positionWS = TransformObjectToWorld(input.positionOS.xyz);
                float3 normalWS = TransformObjectToWorldNormal(input.normalOS);
                float3 biased = ApplyShadowBias(positionWS, normalWS, _LightDirection);
                output.positionCS = TransformWorldToHClip(biased);
                return output;
            }

            half4 frag(Varyings input) : SV_Target
            {
                return 0;
            }
            ENDHLSL
        }

        Pass
        {
            Name "DepthOnly"
            Tags { "LightMode" = "DepthOnly" }

            ZWrite On
            ColorMask 0

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct Attributes { float4 positionOS : POSITION; };
            struct Varyings { float4 positionCS : SV_POSITION; };

            Varyings vert(Attributes input)
            {
                Varyings output;
                output.positionCS = TransformObjectToHClip(input.positionOS.xyz);
                return output;
            }

            half4 frag(Varyings input) : SV_Target
            {
                return 0;
            }
            ENDHLSL
        }

        Pass
        {
            Name "DepthNormals"
            Tags { "LightMode" = "DepthNormals" }

            ZWrite On

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 normalWS : TEXCOORD0;
            };

            Varyings vert(Attributes input)
            {
                Varyings output;
                output.positionCS = TransformObjectToHClip(input.positionOS.xyz);
                output.normalWS = TransformObjectToWorldNormal(input.normalOS);
                return output;
            }

            half4 frag(Varyings input) : SV_Target
            {
                return half4(normalize(input.normalWS), 0.0);
            }
            ENDHLSL
        }
    }

    FallBack "Universal Render Pipeline/Lit"
}
