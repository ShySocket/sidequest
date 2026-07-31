Shader "Sidequest/RearCameraBackground"
{
    // Draws a full-screen texture (rear camera feed, or a video RenderTexture)
    // behind everything else.
    //
    // The two passes below are not redundant. This project renders with URP's
    // 2D Renderer, which only draws passes tagged "Universal2D" - a shader with
    // just a "UniversalForward" pass compiles and binds fine but is silently
    // never drawn, showing a blank background with no error anywhere. Shipping
    // both tags keeps the shader working under either renderer, so moving to a
    // 3D renderer later does not break it again.
    Properties
    {
        _MainTex ("Camera Texture", 2D) = "white" {}
    }

    SubShader
    {
        Tags
        {
            "RenderPipeline" = "UniversalPipeline"
            "RenderType" = "Opaque"
            "Queue" = "Geometry-100"
        }

        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

        TEXTURE2D(_MainTex);
        SAMPLER(sampler_MainTex);

        CBUFFER_START(UnityPerMaterial)
            float4 _MainTex_ST;
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
        };

        Varyings Vertex(Attributes input)
        {
            Varyings output;
            output.positionCS = TransformObjectToHClip(input.positionOS.xyz);
            output.uv = input.uv * _MainTex_ST.xy + _MainTex_ST.zw;
            return output;
        }

        half4 Fragment(Varyings input) : SV_Target
        {
            return SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, input.uv);
        }
        ENDHLSL

        Pass
        {
            Name "RearCameraBackground2D"
            Tags { "LightMode" = "Universal2D" }

            Cull Off
            ZWrite On
            ZTest LEqual

            HLSLPROGRAM
            #pragma vertex Vertex
            #pragma fragment Fragment
            ENDHLSL
        }

        Pass
        {
            Name "RearCameraBackgroundForward"
            Tags { "LightMode" = "UniversalForward" }

            Cull Off
            ZWrite On
            ZTest LEqual

            HLSLPROGRAM
            #pragma vertex Vertex
            #pragma fragment Fragment
            ENDHLSL
        }
    }

    Fallback Off
}
