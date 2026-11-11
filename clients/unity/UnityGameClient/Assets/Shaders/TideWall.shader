Shader "AI003/TideWall"
{
    Properties
    {
        _Color ("Dust Colour", Color) = (0.72, 0.50, 0.30, 1)
        _TopColor ("Top Colour", Color) = (0.55, 0.42, 0.32, 1)
        _Alpha ("Alpha", Range(0, 1)) = 0.80
        _Scroll ("Scroll Speed", Float) = 0.35
        _NoiseScale ("Noise Scale", Float) = 30
        _Softness ("Top Softness", Range(0, 1)) = 0.35
    }

    SubShader
    {
        Tags
        {
            "RenderType" = "Transparent"
            "RenderPipeline" = "UniversalPipeline"
            "Queue" = "Transparent"
        }

        Pass
        {
            Name "TideWall"
            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            Cull Off

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float4 _Color;
                float4 _TopColor;
                float _Alpha;
                float _Scroll;
                float _NoiseScale;
                float _Softness;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv : TEXCOORD0;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
                float3 positionWS : TEXCOORD1;
            };

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
                VertexPositionInputs positions = GetVertexPositionInputs(input.positionOS.xyz);
                output.positionCS = positions.positionCS;
                output.positionWS = positions.positionWS;
                output.uv = input.uv;
                return output;
            }

            half4 frag(Varyings input) : SV_Target
            {
                float2 uv = input.uv;
                float t = _Time.y * _Scroll;

                // Two scrolling octaves give the wall its turbulent face.
                float n1 = ValueNoise(float2(uv.x * _NoiseScale * 3.0 + t, uv.y * _NoiseScale));
                float n2 = ValueNoise(float2(uv.x * _NoiseScale * 9.0 - t * 1.7, uv.y * _NoiseScale * 3.0));
                float density = saturate(n1 * 0.65 + n2 * 0.45);

                // Denser at the base, dissolving at the top.
                float vertical = saturate(1.0 - uv.y * (1.0 - _Softness) * 1.4);
                float alpha = saturate(density * vertical) * _Alpha;
                alpha *= smoothstep(0.0, 0.08, uv.x) * smoothstep(1.0, 0.92, uv.x);

                // Backlit dust: brighter towards the top where the sun grazes it.
                float lift = saturate(uv.y * 0.8 + 0.25);
                float3 color = lerp(_TopColor.rgb, _Color.rgb, vertical) * (0.70 + 0.75 * density);
                color *= lerp(0.85, 1.35, lift);
                return half4(color, alpha);
            }
            ENDHLSL
        }
    }
    FallBack Off
}
