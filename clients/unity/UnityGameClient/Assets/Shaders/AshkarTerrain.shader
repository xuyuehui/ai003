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
        [NoScaleOffset] _DetailNormal ("Detail Normal", 2D) = "gray" {}
        _DetailScale1 ("Detail Scale 1", Float) = 0.09
        _DetailScale2 ("Detail Scale 2", Float) = 0.37
        _DetailStrength ("Detail Strength", Range(0, 2)) = 0.5
        [Header(Surface layers)]
        _SurfaceStrength ("Surface Texture Strength", Range(0, 1)) = 0.55
        _SurfaceNormalStrength ("Surface Normal Strength", Range(0, 2)) = 0.35
        _SaltHeight ("Salt Band Height (m)", Float) = 3.0
        _SandFineTile ("Sand Fine Tile (m)", Float) = 0.6
        _SandMidTile ("Sand Mid Tile (m)", Float) = 2.5
        _SandCoarseTile ("Sand Coarse Tile (m)", Float) = 10.0
        _SaltTile ("Salt Tile (m)", Float) = 2.0
        _RockTile ("Rock Tile (m)", Float) = 4.0
        [NoScaleOffset] _SandFineAlbedo ("Sand Fine Albedo", 2D) = "white" {}
        [NoScaleOffset] _SandFineNormal ("Sand Fine Normal", 2D) = "bump" {}
        [NoScaleOffset] _SandMidAlbedo ("Sand Mid Albedo", 2D) = "white" {}
        [NoScaleOffset] _SandMidNormal ("Sand Mid Normal", 2D) = "bump" {}
        [NoScaleOffset] _SandCoarseAlbedo ("Sand Coarse Albedo", 2D) = "white" {}
        [NoScaleOffset] _SandCoarseNormal ("Sand Coarse Normal", 2D) = "bump" {}
        [NoScaleOffset] _SaltAlbedo ("Salt Albedo", 2D) = "white" {}
        [NoScaleOffset] _SaltNormal ("Salt Normal", 2D) = "bump" {}
        [NoScaleOffset] _RockAlbedo ("Rock Albedo", 2D) = "white" {}
        [NoScaleOffset] _RockNormal ("Rock Normal", 2D) = "bump" {}
        [Header(Snow cap)]
        _SnowLine ("Snow Line (m)", Float) = 254.7
        _SnowBlend ("Snow Line Blend (m)", Float) = 22.0
        _SnowTile ("Snow Tile (m)", Float) = 2.0
        [NoScaleOffset] _SnowAlbedo ("Snow Albedo", 2D) = "white" {}
        [NoScaleOffset] _SnowNormal ("Snow Normal", 2D) = "bump" {}
        [Header(Steppe grass)]
        _GrassTile ("Grass Tile (m)", Float) = 2.0
        _GrassStrength ("Grass Strength", Range(0, 1)) = 0.45
        [NoScaleOffset] _GrassAlbedo ("Grass Albedo", 2D) = "white" {}
        [NoScaleOffset] _GrassNormal ("Grass Normal", 2D) = "bump" {}
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

            TEXTURE2D(_DetailNormal);
            SAMPLER(sampler_DetailNormal);
            TEXTURE2D(_SandFineAlbedo);   SAMPLER(sampler_SandFineAlbedo);
            TEXTURE2D(_SandFineNormal);   SAMPLER(sampler_SandFineNormal);
            TEXTURE2D(_SandMidAlbedo);    SAMPLER(sampler_SandMidAlbedo);
            TEXTURE2D(_SandMidNormal);    SAMPLER(sampler_SandMidNormal);
            TEXTURE2D(_SandCoarseAlbedo); SAMPLER(sampler_SandCoarseAlbedo);
            TEXTURE2D(_SandCoarseNormal); SAMPLER(sampler_SandCoarseNormal);
            TEXTURE2D(_SaltAlbedo);       SAMPLER(sampler_SaltAlbedo);
            TEXTURE2D(_SaltNormal);       SAMPLER(sampler_SaltNormal);
            TEXTURE2D(_RockAlbedo);       SAMPLER(sampler_RockAlbedo);
            TEXTURE2D(_RockNormal);       SAMPLER(sampler_RockNormal);
            TEXTURE2D(_SnowAlbedo);       SAMPLER(sampler_SnowAlbedo);
            TEXTURE2D(_SnowNormal);       SAMPLER(sampler_SnowNormal);
            TEXTURE2D(_GrassAlbedo);      SAMPLER(sampler_GrassAlbedo);
            TEXTURE2D(_GrassNormal);      SAMPLER(sampler_GrassNormal);

            CBUFFER_START(UnityPerMaterial)
                float _Smoothness;
                float _RippleStrength;
                float _Speckle;
                float _Wrap;
                float4 _HeightFogColor;
                float _HeightFogStrength;
                float _HeightFogFalloff;
                float _HeightFogDistance;
                float _DetailScale1;
                float _DetailScale2;
                float _DetailStrength;
                float _SurfaceStrength;
                float _SurfaceNormalStrength;
                float _SaltHeight;
                float _SandFineTile;
                float _SandMidTile;
                float _SandCoarseTile;
                float _SaltTile;
                float _RockTile;
                float _SnowLine;
                float _SnowBlend;
                float _SnowTile;
                float _GrassTile;
                float _GrassStrength;
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

                // Two scales of a tiling detail normal: wind-scoured micro relief.
                // This is what stops a 4 m mesh from reading as melted plastic.
                float3 d1 = SAMPLE_TEXTURE2D(_DetailNormal, sampler_DetailNormal, p * _DetailScale1).rgb * 2.0 - 1.0;
                float3 d2 = SAMPLE_TEXTURE2D(_DetailNormal, sampler_DetailNormal, p * _DetailScale2).rgb * 2.0 - 1.0;
                float detailFade = saturate(1.0 - dist / 420.0);
                float2 detail = (d1.xy * 0.75 + d2.xy * 0.45) * _DetailStrength * up * up * detailFade;
                normalWS = normalize(normalWS + float3(detail.x, 0.0, detail.y));

                // Surface layers: five scanned sets on world-XZ UVs. Sand blends by
                // distance (fine near, coarse far), salt wins in the low salt band,
                // rock on steep faces. Vertex colours keep the biome palette; the
                // textures add the metre-scale detail a 4 m mesh cannot carry.
                float flatW = smoothstep(0.55, 0.90, up);
                float rockW = 1.0 - flatW;
                float saltW = flatW * saturate(1.0 - input.positionWS.y / max(_SaltHeight, 0.01));
                float sandW = saturate(flatW - saltW);
                float midF = saturate((dist - 30.0) / 90.0);
                float coarseF = saturate((dist - 160.0) / 240.0);

                float2 nMid = SAMPLE_TEXTURE2D(_SandMidNormal, sampler_SandMidNormal, p / _SandMidTile).rg * 2.0 - 1.0;
                float2 nRock = SAMPLE_TEXTURE2D(_RockNormal, sampler_RockNormal, p / _RockTile).rg * 2.0 - 1.0;
                float2 nSalt = SAMPLE_TEXTURE2D(_SaltNormal, sampler_SaltNormal, p / _SaltTile).rg * 2.0 - 1.0;
                float snowW = smoothstep(_SnowLine, _SnowLine + max(_SnowBlend, 0.01), input.positionWS.y) * flatW;
                float2 nSnow = SAMPLE_TEXTURE2D(_SnowNormal, sampler_SnowNormal, p / _SnowTile).rg * 2.0 - 1.0;
                // Steppe: green-dominant vertex colour means grass, not dune or salt.
                // (The vertex colour is the biome palette, so the shader needs no data.)
                float grassW = flatW * (1.0 - snowW)
                               * saturate((input.color.rgb.g - input.color.rgb.b) * 4.0)
                               * _GrassStrength;
                float2 nGrass = SAMPLE_TEXTURE2D(_GrassNormal, sampler_GrassNormal, p / _GrassTile).rg * 2.0 - 1.0;
                float2 surfN = nMid * sandW + nRock * rockW + nSalt * saltW + nSnow * snowW + nGrass * grassW;
                normalWS = normalize(normalWS + float3(surfN.x, 0.0, surfN.y) * _SurfaceNormalStrength * up);

                float4 shadowCoord = TransformWorldToShadowCoord(input.positionWS);
                Light mainLight = GetMainLight(shadowCoord);

                float ndl = saturate(dot(normalWS, mainLight.direction));
                float diff = saturate((ndl + _Wrap) / (1.0 + _Wrap));

                // Grain: two octaves of value noise keep big flat dunes alive.
                float grain = ValueNoise(p * 0.85) * 0.62 + ValueNoise(p * 3.7) * 0.38;
                float3 albedo = input.color.rgb * ((1.0 - _Speckle * 0.5) + grain * _Speckle);

                // Same layer weights as the normal block: the albedo detail is the
                // distance-blended sand trio, with salt/rock on their own faces.
                float3 sandA = SAMPLE_TEXTURE2D(_SandFineAlbedo, sampler_SandFineAlbedo, p / _SandFineTile).rgb;
                sandA = lerp(sandA, SAMPLE_TEXTURE2D(_SandMidAlbedo, sampler_SandMidAlbedo, p / _SandMidTile).rgb, midF);
                sandA = lerp(sandA, SAMPLE_TEXTURE2D(_SandCoarseAlbedo, sampler_SandCoarseAlbedo, p / _SandCoarseTile).rgb, coarseF);
                float3 saltA = SAMPLE_TEXTURE2D(_SaltAlbedo, sampler_SaltAlbedo, p / _SaltTile).rgb;
                float3 rockA = SAMPLE_TEXTURE2D(_RockAlbedo, sampler_RockAlbedo, p / _RockTile).rgb;
                float3 surfA = (sandA * sandW + saltA * saltW + rockA * rockW)
                               / max(sandW + saltW + rockW, 1e-4);
                float3 grassA = SAMPLE_TEXTURE2D(_GrassAlbedo, sampler_GrassAlbedo, p / _GrassTile).rgb;
                surfA = lerp(surfA, grassA, saturate(grassW));
                // Normalise around mid-grey so the layer modulates the theme colour
                // instead of replacing it (a texture must not repaint the biome).
                float3 surfMod = saturate(surfA / 0.45);
                albedo *= lerp(float3(1.0, 1.0, 1.0), surfMod, _SurfaceStrength);

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
