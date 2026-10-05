// DepthWizardWater.shader  (drop-in replacement, same shader name + same _Color property
// so the existing DepthWizard_Water.mat keeps working)
//
// Still cheap and unlit-pipeline-friendly, but adds what the flat version lacked:
//   - Fresnel: grazing angles reflect the sky, steep angles show the water colour
//   - Sun glint: Blinn-Phong highlight from the main directional light
//   - Procedural animated normals (sum of sine waves, no texture needed)
//   - Fog: matches the terrain's fog so the plane doesn't "pop" at distance
//
// The reflected sky is recomputed from the same 3 colours the gradient skybox uses
// (no reflection-probe dependency), so keep _SkyColor/_HorizonColor in sync with
// DepthWizard_GradientSky.mat.
//
// NOTE: written without access to a Unity editor - not compiled/tested. If the Console
// reports an error, paste it back and it should be a one-line fix.
Shader "DepthWizard/Water"
{
    Properties
    {
        _Color("Water Color", Color) = (0.15, 0.35, 0.5, 0.75)
        _SkyColor("Reflected Sky Color", Color) = (0.35, 0.55, 0.85, 1)
        _HorizonColor("Reflected Horizon Color", Color) = (0.75, 0.8, 0.85, 1)
        _FresnelPower("Fresnel Power", Range(0.5, 8)) = 3
        _ReflectionStrength("Reflection Strength", Range(0, 1)) = 0.9
        _SpecPower("Sun Glint Sharpness", Range(8, 2048)) = 400
        _SpecIntensity("Sun Glint Intensity", Range(0, 10)) = 3
        _WaveScale("Wave Scale (1/m)", Range(0.005, 0.5)) = 0.06
        _WaveStrength("Wave Strength", Range(0, 0.5)) = 0.12
        _WaveSpeed("Wave Speed", Range(0, 3)) = 0.6
        _WaveFadeDistance("Wave Fade Distance (m)", Float) = 4000
    }

    SubShader
    {
        Tags { "RenderType" = "Transparent" "Queue" = "Transparent" "RenderPipeline" = "UniversalPipeline" }
        Blend SrcAlpha OneMinusSrcAlpha
        ZWrite Off
        Cull Back

        Pass
        {
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_fog
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                float  fogFactor  : TEXCOORD1;
            };

            CBUFFER_START(UnityPerMaterial)
            half4 _Color;
            half4 _SkyColor;
            half4 _HorizonColor;
            float _FresnelPower;
            float _ReflectionStrength;
            float _SpecPower;
            float _SpecIntensity;
            float _WaveScale;
            float _WaveStrength;
            float _WaveSpeed;
            float _WaveFadeDistance;
            CBUFFER_END

            Varyings vert(Attributes IN)
            {
                Varyings OUT;
                VertexPositionInputs p = GetVertexPositionInputs(IN.positionOS.xyz);
                OUT.positionCS = p.positionCS;
                OUT.positionWS = p.positionWS;
                OUT.fogFactor = ComputeFogFactor(p.positionCS.z);
                return OUT;
            }

            // Analytic gradient of three scrolling sine waves -> perturbed up-vector.
            float3 WaveNormal(float2 xz, float t, float strength)
            {
                const float2 d1 = float2(0.80, 0.60);
                const float2 d2 = float2(-0.50, 0.85);
                const float2 d3 = float2(0.30, -0.95);

                float2 g = 0.0;
                g += d1 * 1.00 * cos(dot(d1, xz) * 1.00 + t * 1.0);
                g += d2 * 0.60 * cos(dot(d2, xz) * 1.71 + t * 1.3);
                g += d3 * 0.35 * cos(dot(d3, xz) * 2.93 + t * 1.9);

                return normalize(float3(-g.x * strength, 1.0, -g.y * strength));
            }

            half4 frag(Varyings IN) : SV_Target
            {
                float3 toCam = _WorldSpaceCameraPos - IN.positionWS;
                float dist = length(toCam);
                float3 V = toCam / max(dist, 1e-4);

                // Fade waves out with distance so far water doesn't shimmer/alias.
                float fade = saturate(1.0 - dist / max(_WaveFadeDistance, 1.0));
                float3 N = WaveNormal(IN.positionWS.xz * _WaveScale, _Time.y * _WaveSpeed, _WaveStrength * fade);

                float3 L = normalize(_MainLightPosition.xyz); // direction TO the sun
                float3 H = normalize(L + V);

                // Fresnel: more reflection at grazing angles.
                float fresnel = pow(1.0 - saturate(dot(N, V)), _FresnelPower);

                // Reflected sky: same gradient maths as DepthWizardGradientSky (default exponent 1.5).
                float3 R = reflect(-V, N);
                float h = saturate(R.y);
                float3 sky = lerp(_HorizonColor.rgb, _SkyColor.rgb, saturate(pow(saturate(h * 2.0), 1.5)));

                // Sun glint (only when the sun is above the horizon and lights this normal).
                float sunUp = saturate(L.y * 10.0);
                float spec = pow(saturate(dot(N, H)), _SpecPower) * _SpecIntensity
                           * sunUp * saturate(dot(N, L) * 4.0);

                float3 body = _Color.rgb * (0.7 + 0.3 * saturate(dot(N, L)));
                float3 col = lerp(body, sky, saturate(fresnel * _ReflectionStrength));
                col += _MainLightColor.rgb * spec;

                float alpha = saturate(_Color.a + fresnel * 0.3 + spec);

                col = MixFog(col, IN.fogFactor);
                return half4(col, alpha);
            }
            ENDHLSL
        }
    }
}
