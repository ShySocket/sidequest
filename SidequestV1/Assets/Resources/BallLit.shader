Shader "Sidequest/BallLit"
{
    // Lighting for the character ball, hand-written rather than URP/Lit.
    //
    // Two reasons. This project renders through URP's 2D Renderer, which draws
    // no "UniversalForward" pass, so URP/Lit is simply invisible here - and
    // switching the project to a 3D renderer to fix that would put the existing
    // 2D prototype at risk. Shipping both pass tags works under either renderer.
    //
    // The second reason matters more: the light has to match a specific video,
    // not a scene. Exposing direction and colour as material properties lets the
    // ball be lit by the same low warm sun visible in the footage, which is what
    // makes it sit in the world rather than on top of it.
    //
    // A black ball has almost no diffuse response, so what actually reads as
    // three-dimensional is the specular highlight and the fresnel rim. Those
    // carry the shading here; the albedo is nearly zero by design.
    // Tuned to the clip's golden-hour light: a low warm sun (the long shadows
    // fall right and toward the camera), a cool blue sky fill on the shadow
    // side, and warm concrete bounce from below. The specular is tight and
    // restrained - a big soft glint read as studio plastic, while a small hot
    // one reads as sun on rubber - and the base colour is very dark grey
    // rather than pure black, because nothing physical reflects nothing.
    Properties
    {
        _BaseColor      ("Base Colour", Color)          = (0.035, 0.034, 0.038, 1)
        _LightDirection ("Light Direction", Vector)     = (0.55, -0.65, 0.35, 0)
        _LightColor     ("Light Colour", Color)         = (1.05, 0.92, 0.72, 1)
        _SkyColor       ("Ambient Sky", Color)          = (0.30, 0.40, 0.58, 1)
        _GroundColor    ("Ambient Ground", Color)       = (0.34, 0.28, 0.20, 1)
        _SpecColor      ("Specular Colour", Color)      = (1.0, 0.94, 0.82, 1)
        _Smoothness     ("Smoothness", Range(0, 1))     = 0.88
        _SpecStrength   ("Specular Strength", Range(0, 4)) = 1.2
        _RimColor       ("Rim Colour", Color)           = (0.60, 0.64, 0.72, 1)
        _RimPower       ("Rim Power", Range(0.5, 8))    = 4.0
        _RimStrength    ("Rim Strength", Range(0, 2))   = 0.5
    }

    SubShader
    {
        Tags
        {
            "RenderPipeline" = "UniversalPipeline"
            "RenderType" = "Opaque"
            "Queue" = "Geometry"
        }

        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

        CBUFFER_START(UnityPerMaterial)
            float4 _BaseColor;
            float4 _LightDirection;
            float4 _LightColor;
            float4 _SkyColor;
            float4 _GroundColor;
            float4 _SpecColor;
            float  _Smoothness;
            float  _SpecStrength;
            float4 _RimColor;
            float  _RimPower;
            float  _RimStrength;
        CBUFFER_END

        struct Attributes
        {
            float4 positionOS : POSITION;
            float3 normalOS   : NORMAL;
        };

        struct Varyings
        {
            float4 positionCS : SV_POSITION;
            float3 normalWS   : TEXCOORD0;
            float3 positionWS : TEXCOORD1;
        };

        Varyings Vertex(Attributes input)
        {
            Varyings output;
            float3 positionWS = TransformObjectToWorld(input.positionOS.xyz);
            output.positionWS = positionWS;
            output.positionCS = TransformWorldToHClip(positionWS);
            output.normalWS = TransformObjectToWorldNormal(input.normalOS);
            return output;
        }

        half4 Fragment(Varyings input) : SV_Target
        {
            float3 N = normalize(input.normalWS);
            // _LightDirection is the direction light travels, so the vector
            // toward the light is its negation.
            float3 L = normalize(-_LightDirection.xyz);
            // GetWorldSpaceNormalizeViewDir handles the orthographic camera this
            // scene uses, where the view vector is constant across the frame.
            float3 V = GetWorldSpaceNormalizeViewDir(input.positionWS);
            float3 H = normalize(L + V);

            // Hemispheric ambient: sky above, bounced ground light below. Cheap,
            // and it keeps the underside from going pure black.
            float hemisphere = saturate(N.y * 0.5 + 0.5);
            float3 ambient = lerp(_GroundColor.rgb, _SkyColor.rgb, hemisphere);

            // Wrapped diffuse: outdoor light is never a point source - sky
            // and bounce soften the terminator, and a hard day/night line
            // across a small ball is what reads as "rendered".
            float diffuse = saturate((dot(N, L) + 0.25) / 1.25);
            float3 colour = _BaseColor.rgb * (ambient + _LightColor.rgb * diffuse);

            float exponent = exp2(_Smoothness * 11.0) + 2.0;
            float specular = pow(saturate(dot(N, H)), exponent);
            colour += _SpecColor.rgb * specular * _SpecStrength * saturate(diffuse * 4.0);

            float fresnel = pow(1.0 - saturate(dot(N, V)), _RimPower);
            colour += _RimColor.rgb * fresnel * _RimStrength;

            return half4(colour, 1.0);
        }
        ENDHLSL

        Pass
        {
            Name "BallLit2D"
            Tags { "LightMode" = "Universal2D" }
            HLSLPROGRAM
            #pragma vertex Vertex
            #pragma fragment Fragment
            ENDHLSL
        }

        Pass
        {
            Name "BallLitForward"
            Tags { "LightMode" = "UniversalForward" }
            HLSLPROGRAM
            #pragma vertex Vertex
            #pragma fragment Fragment
            ENDHLSL
        }
    }

    Fallback Off
}
