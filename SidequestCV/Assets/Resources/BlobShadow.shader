Shader "Sidequest/BlobShadow"
{
    // The ball's contact shadow.
    //
    // Deliberately not a real shadow map. A cast shadow needs a surface to land
    // on, and there is no geometry here - the ground is pixels in a video, on a
    // perspective plane that no flat receiver in the scene matches. A shadow map
    // would land on the wrong place or on nothing.
    //
    // A blob drawn at the ground line is both cheaper and more correct: it can be
    // placed exactly where the level says the ground is. Its size and opacity
    // track the ball's height, which is what actually communicates altitude -
    // without it, a jump reads as the ball growing rather than leaving the ground.
    Properties
    {
        _Color   ("Colour", Color)             = (0, 0, 0, 1)
        _Opacity ("Opacity", Range(0, 1))      = 0.62
        _Falloff ("Edge Falloff", Range(0.5, 8)) = 1.5
    }

    SubShader
    {
        Tags
        {
            "RenderPipeline" = "UniversalPipeline"
            "RenderType" = "Transparent"
            "Queue" = "Transparent"
        }

        Blend SrcAlpha OneMinusSrcAlpha
        ZWrite Off
        Cull Off

        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

        CBUFFER_START(UnityPerMaterial)
            float4 _Color;
            float  _Opacity;
            float  _Falloff;
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
            output.uv = input.uv;
            return output;
        }

        half4 Fragment(Varyings input) : SV_Target
        {
            // Radial falloff generated in the shader, so no texture asset has to
            // exist or be kept in sync with the material.
            float2 centred = input.uv * 2.0 - 1.0;
            float radius = saturate(length(centred));
            float alpha = pow(1.0 - radius, _Falloff) * _Opacity;
            return half4(_Color.rgb, alpha);
        }
        ENDHLSL

        Pass
        {
            Name "BlobShadow2D"
            Tags { "LightMode" = "Universal2D" }
            HLSLPROGRAM
            #pragma vertex Vertex
            #pragma fragment Fragment
            ENDHLSL
        }

        Pass
        {
            Name "BlobShadowForward"
            Tags { "LightMode" = "UniversalForward" }
            HLSLPROGRAM
            #pragma vertex Vertex
            #pragma fragment Fragment
            ENDHLSL
        }
    }

    Fallback Off
}
