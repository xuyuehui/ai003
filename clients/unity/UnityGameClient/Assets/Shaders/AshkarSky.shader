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
            CBUFFER_END

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
                float3 sky = lerp(_Horizon.rgb, _Zenith.rgb, pow(up, 0.30));
                float haze = exp(-abs(d.y) * 16.0) * _Haze;
                sky = lerp(sky, _Horizon.rgb * 1.08, saturate(haze));

                // A broad, soft dust band just above the horizon.
                float band = exp(-pow((d.y - 0.06) * 9.0, 2.0)) * _DustBand;
                sky += _Horizon.rgb * band * 0.35;

                // Below the horizon: ground haze, not a hard cut.
                float g = saturate(-d.y * 2.5);
                sky = lerp(sky, _Ground.rgb, g * g);

                float s1 = saturate(dot(d, normalize(_SunDir.xyz)));
                float s2 = saturate(dot(d, normalize(_Sun2Dir.xyz)));

                float disk1 = smoothstep(_SunSize, _SunSize + 0.00035, s1);
                float disk2 = smoothstep(_Sun2Size, _Sun2Size + 0.00055, s2);

                float glow1 = pow(s1, 260.0) * 0.45 + pow(s1, 26.0) * 0.10;
                float glow2 = pow(s2, 700.0) * 0.30 + pow(s2, 40.0) * 0.05;

                float3 col = sky
                    + _SunColor.rgb * (disk1 * 8.0 + glow1)
                    + _Sun2Color.rgb * (disk2 * 5.0 + glow2);

                return float4(col, 1.0);
            }
            ENDHLSL
        }
    }
    FallBack Off
}
