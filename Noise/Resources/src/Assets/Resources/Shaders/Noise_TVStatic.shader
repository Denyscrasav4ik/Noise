Shader "Hidden/URP/Noise/TVStatic"
{
    Properties
    {
        [PerRendererData] _MainTex ("Sprite Texture", 2D) = "white" {}
        _Color ("Tint", Color) = (1,1,1,1)
        _Speed ("Static Speed", Float) = 25.0
        _Scale ("Grain Resolution", Float) = 300.0
        _StaticIntensity ("Static Blend", Range(0, 1)) = 1.0

        _OutlineColor ("Outline Color", Color) = (0,0,0,1)
        _OutlineWidth ("Outline Width", Float) = 1.0

        _IsPaused ("Is Paused", Float) = 0
        _IsFastForward ("Is Fast Forward", Float) = 0

        [HideInInspector] _RendererColor ("RendererColor", Color) = (1,1,1,1)
        [HideInInspector] _Flip ("Flip", Vector) = (1,1,1,1)
    }

    SubShader
    {
        Tags
        {
            "Queue" = "Transparent"
            "IgnoreProjector" = "True"
            "RenderType" = "Transparent"
            "PreviewType" = "Plane"
            "CanUseSpriteAtlas" = "True"
            "RenderPipeline" = "UniversalPipeline"
        }

        Stencil
        {
            Ref 1
            Comp Always
            Pass Replace
        }

        Cull Off
        Lighting Off
        ZWrite Off
        Blend One OneMinusSrcAlpha

        Pass
        {
            Name "TVStaticUnlit"
            HLSLPROGRAM
            #pragma vertex UnlitVertex
            #pragma fragment UnlitFragment

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct Attributes
            {
                float3 positionOS : POSITION;
                float4 color      : COLOR;
                float2 uv         : TEXCOORD0;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float4 color      : COLOR;
                float2 uv         : TEXCOORD0;
            };

            TEXTURE2D(_MainTex);
            SAMPLER(sampler_MainTex);

            CBUFFER_START(UnityPerMaterial)
                float4 _MainTex_ST;
                float4 _MainTex_TexelSize;
                float4 _Color;
                float _Speed;
                float _Scale;
                float _StaticIntensity;
                float4 _OutlineColor;
                float _OutlineWidth;
                float _IsPaused;
                float _IsFastForward;
            CBUFFER_END

            float4 _RendererColor;

            float random(float2 st, float time)
            {
                return frac(sin(dot(st.xy, float2(12.9898, 78.233)) + time) * 43758.5453123);
            }

            Varyings UnlitVertex(Attributes input)
            {
                Varyings output;
                VertexPositionInputs positionInputs = GetVertexPositionInputs(input.positionOS);
                output.positionCS = positionInputs.positionCS;
                output.uv = TRANSFORM_TEX(input.uv, _MainTex);
                output.color = input.color * _Color * _RendererColor;
                return output;
            }

            float4 UnlitFragment(Varyings input) : SV_Target
            {
                float4 mainColor = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, input.uv);

                float2 texel = _MainTex_TexelSize.xy * _OutlineWidth;
                float aU  = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, input.uv + float2(0, texel.y)).a;
                float aD  = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, input.uv + float2(0, -texel.y)).a;
                float aL  = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, input.uv + float2(-texel.x, 0)).a;
                float aR  = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, input.uv + float2(texel.x, 0)).a;
                float aUL = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, input.uv + float2(-texel.x, texel.y)).a;
                float aUR = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, input.uv + float2(texel.x, texel.y)).a;
                float aDL = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, input.uv + float2(-texel.x, -texel.y)).a;
                float aDR = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, input.uv + float2(texel.x, -texel.y)).a;

                float outlineAlpha = max(max(max(aU, aD), max(aL, aR)), max(max(aUL, aUR), max(aDL, aDR)));
                float outlineMask = saturate(outlineAlpha - mainColor.a);

                float frameTime = floor(_Time.y * _Speed);
                float2 gridUV = floor(input.uv * _Scale);
                float noise = random(gridUV, frameTime * 100.0);

                float3 staticRGB = float3(noise, noise, noise);

                staticRGB = lerp(staticRGB, float3(0.0, 0.0, noise), _IsFastForward);

                float4 spriteColor;
                spriteColor.rgb = lerp(mainColor.rgb, staticRGB, _StaticIntensity) * input.color.rgb;
                spriteColor.a = mainColor.a * input.color.a;

                float4 finalColor;
                finalColor.rgb = lerp(_OutlineColor.rgb * input.color.rgb, spriteColor.rgb, mainColor.a);

                finalColor.rgb = lerp(finalColor.rgb, float3(0.0, 0.0, 0.0), _IsPaused);
                finalColor.a = max(spriteColor.a, outlineMask * _OutlineColor.a * input.color.a);

                clip(finalColor.a - 0.001);

                finalColor.rgb *= finalColor.a;

                return finalColor;
            }
            ENDHLSL
        }
    }
}
