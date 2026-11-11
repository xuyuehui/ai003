Shader "AI003/AshkarSky"
{
    Properties
    {
        _Zenith ("Zenith", Color) = (0.10, 0.10, 0.10, 1)
        _Horizon ("Horizon", Color) = (0.95, 0.78, 0.55, 1)
        _Ground ("Ground", Color) = (0.30, 0.22, 0.15, 1)
        _SunDir ("Key Sun Direction", Vector) = (0, 0.12, 1, 0)
        _SunColor ("Key Sun Color", Color) = (1, 0.94, 0.85, 1)
        _SunSize ("Key Sun Disk", Float) = 0.99965
        _Sun2Dir ("Second Sun Direction", Vector) = (-0.4, 0.35, 0.8, 0)
        _Sun2Color ("Second Sun Color", Color) = (0.9, 0.42, 0.22, 1)
        _Sun2Size ("Second Sun Disk", Float) = 0.99980
        _Haze ("Horizon Haze", Float) = 0.55
        _DustBand ("Dust Band", Float) = 0.35
        _Stars ("Stars", Float) = 0
        _FogColor ("Horizon Fog Colour", Color) = (0.9, 0.8, 0.65, 1)
        _HorizonBand ("Horizon Band", Float) = 0.5
    }

    SubShader
    {
        Tags
        {
            "Queue" = "Background"
            "RenderType" = "Background"
            "PreviewType" = "Skybox"
            "RenderPipeline" = "UniversalPipeline"
        }
        Cull Off
        ZWrite Off

        Pass
        {
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 dir : TEXCOORD0;
            };

            CBUFFER_START(UnityPerMaterial)
                float4 _Zenith;
                float4 _Horizon;
                float4 _Ground;
                float4 _SunDir;
                float4 _SunColor;
                float _SunSize;
                float4 _Sun2Dir;
                float4 _Sun2Color;
                float _Sun2Size;
                float _Haze;
                float _DustBand;
                float _Stars;
                float4 _FogColor;
                float _HorizonBand;
            CBUFFER_END

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
                return lerp(lerp(Hash21(i), Hash21(i + float2(1, 0)), f.x),
                            lerp(Hash21(i + float2(0, 1)), Hash21(i + float2(1, 1)), f.x), f.y);
            }

            Varyings vert(Attributes input)
            {
                Varyings output;
                output.positionCS = TransformObjectToHClip(input.positionOS.xyz);
                output.dir = input.positionOS.xyz;
                return output;
            }

            float4 frag(Varyings input) : SV_Target
            {
                float3 d = normalize(input.dir);
                float up = saturate(d.y);

                // Dust sky: horizon glow fading into a cooler, greyer zenith.
                // 0.55 keeps the warm band near the horizon without snapping to a
                // dark zenith as soon as the camera tilts up.
                float3 sky = lerp(_Horizon.rgb, _Zenith.rgb, pow(up, 0.55));
                float haze = exp(-abs(d.y) * 16.0) * _Haze;
                sky = lerp(sky, _Horizon.rgb * 1.08, saturate(haze));

                // Layered dust: two soft strata with slow noise so the sky is not
                // a single flat gradient.
                float n1 = ValueNoise(d.xz * 5.0 / max(d.y + 0.15, 0.15)) * 0.6
                           + ValueNoise(d.xz * 13.0) * 0.4;
                float band1 = exp(-pow((d.y - 0.055) * 11.0, 2.0));
                float band2 = exp(-pow((d.y - 0.150) * 20.0, 2.0));
                sky += _Horizon.rgb * _DustBand * (band1 * 0.55 + band2 * 0.25) * (0.55 + 0.45 * n1);

                // Faint sun halo, so the disk sits inside the atmosphere.
                float halo = pow(saturate(dot(d, normalize(_SunDir.xyz))), 6.0) * 0.10;
                sky += _SunColor.rgb * halo;

                // Knit the sky into the fogged ground: without this the sea
                // horizon reads as a hard band of colour.
                float knit = exp(-abs(d.y) * 90.0) * _HorizonBand;
                sky = lerp(sky, _FogColor.rgb, saturate(knit));

                // Below the horizon the sky must read as the same haze the fog gives
                // distant terrain — otherwise it shows up as a flat brown slab where
                // the 2 km map ends.
                float g = smoothstep(0.12, -0.02, d.y);
                sky = lerp(sky, _Ground.rgb, g);

                float s1 = saturate(dot(d, normalize(_SunDir.xyz)));
                float s2 = saturate(dot(d, normalize(_Sun2Dir.xyz)));

                float disk1 = smoothstep(_SunSize, _SunSize + 0.00035, s1);
                float disk2 = smoothstep(_Sun2Size, _Sun2Size + 0.00055, s2);

                float glow1 = pow(s1, 260.0) * 0.45 + pow(s1, 26.0) * 0.10;
                float glow2 = pow(s2, 700.0) * 0.30 + pow(s2, 40.0) * 0.05;

                float3 col = sky
                    + _SunColor.rgb * (disk1 * 8.0 + glow1)
                    + _Sun2Color.rgb * (disk2 * 5.0 + glow2);

                // Sparse stars, only above the horizon and only when asked for.
                if (_Stars > 0.001)
                {
                    float3 cell = floor(d * 260.0);
                    float hsh = frac(sin(dot(cell, float3(12.9898, 78.233, 37.719))) * 43758.5453);
                    float twinkle = smoothstep(0.9976, 1.0, hsh);
                    col += twinkle * _Stars * saturate(d.y * 1.6) * float3(0.72, 0.82, 1.0);
                }

                return float4(col, 1.0);
            }
            ENDHLSL
        }
    }
    FallBack Off
}
