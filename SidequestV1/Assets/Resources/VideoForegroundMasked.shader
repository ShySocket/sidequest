Shader "Sidequest/VideoForegroundMasked"
{
    // Re-draws a strip of the video in front of the ball, letting only the
    // occluding object's silhouette through. The strip's pixels are identical
    // to the background beneath, so where alpha is 1 the ball simply
    // disappears behind the object - and where the mask says "not the object"
    // the ball stays visible, which is what lets it slide behind a pole's
    // actual outline instead of vanishing at its detection rectangle.
    //
    // _MaskRect maps the quad's uv onto the object's cell in the occluder
    // atlas: xy = cell origin, zw = cell size (GL convention, v up). With no
    // atlas bound the default white texture makes every pixel occlude, which
    // is exactly the old rectangle behavior.
    //
    // Two passes for the same reason as RearCameraBackground: URP's 2D
    // Renderer only draws "Universal2D" passes, a 3D renderer only
    // "UniversalForward" - shipping both keeps it working under either.
    Properties
    {
        _MainTex ("Video Texture", 2D) = "white" {}
        _MaskTex ("Silhouette Atlas", 2D) = "white" {}
        _MaskRect ("Mask Cell (origin, size)", Vector) = (0, 0, 1, 1)
    }

    SubShader
    {
        Tags
        {
            "RenderPipeline" = "UniversalPipeline"
            "RenderType" = "Transparent"
            "Queue" = "Transparent"
        }

        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

        TEXTURE2D(_MainTex);
        SAMPLER(sampler_MainTex);
        TEXTURE2D(_MaskTex);
        SAMPLER(sampler_MaskTex);

        CBUFFER_START(UnityPerMaterial)
            float4 _MainTex_ST;
            float4 _MaskRect;
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
            float2 rawUv : TEXCOORD1;
        };

        Varyings Vertex(Attributes input)
        {
            Varyings output;
            output.positionCS = TransformObjectToHClip(input.positionOS.xyz);
            output.uv = input.uv * _MainTex_ST.xy + _MainTex_ST.zw;
            output.rawUv = input.uv;
            return output;
        }

        half4 Fragment(Varyings input) : SV_Target
        {
            half4 video = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, input.uv);
            float2 maskUv = _MaskRect.xy + input.rawUv * _MaskRect.zw;
            half silhouette = SAMPLE_TEXTURE2D(_MaskTex, sampler_MaskTex, maskUv).r;
            // Feathered rather than hard-cut: the atlas cell is upscaled at
            // render time, and a smoothstepped edge hides the resampling.
            video.a = smoothstep(0.35h, 0.65h, silhouette);
            return video;
        }
        ENDHLSL

        Pass
        {
            Name "VideoForegroundMasked2D"
            Tags { "LightMode" = "Universal2D" }

            Cull Off
            ZWrite Off
            ZTest LEqual
            Blend SrcAlpha OneMinusSrcAlpha

            HLSLPROGRAM
            #pragma vertex Vertex
            #pragma fragment Fragment
            ENDHLSL
        }

        Pass
        {
            Name "VideoForegroundMaskedForward"
            Tags { "LightMode" = "UniversalForward" }

            Cull Off
            ZWrite Off
            ZTest LEqual
            Blend SrcAlpha OneMinusSrcAlpha

            HLSLPROGRAM
            #pragma vertex Vertex
            #pragma fragment Fragment
            ENDHLSL
        }
    }

    Fallback Off
}
