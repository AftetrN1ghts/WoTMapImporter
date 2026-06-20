// URP terrain shader that blends up to 16 layers in a single pass.
Shader "WoT/TerrainMultilayer"
{
    Properties
    {
        [HideInInspector] _Control0 ("Control0", 2D) = "black" {}
        [HideInInspector] _Control1 ("Control1", 2D) = "black" {}
        [HideInInspector] _Control2 ("Control2", 2D) = "black" {}
        [HideInInspector] _Control3 ("Control3", 2D) = "black" {}
        [HideInInspector] _GlobalMap ("Global AM", 2D) = "white" {}
        _UseGlobalMap ("Use Global AM", Float) = 0

        [HideInInspector] _Splat0 ("Splat0", 2D) = "white" {}
        [HideInInspector] _Splat1 ("Splat1", 2D) = "white" {}
        [HideInInspector] _Splat2 ("Splat2", 2D) = "white" {}
        [HideInInspector] _Splat3 ("Splat3", 2D) = "white" {}
        [HideInInspector] _Splat4 ("Splat4", 2D) = "white" {}
        [HideInInspector] _Splat5 ("Splat5", 2D) = "white" {}
        [HideInInspector] _Splat6 ("Splat6", 2D) = "white" {}
        [HideInInspector] _Splat7 ("Splat7", 2D) = "white" {}
        [HideInInspector] _Splat8 ("Splat8", 2D) = "white" {}
        [HideInInspector] _Splat9 ("Splat9", 2D) = "white" {}
        [HideInInspector] _Splat10 ("Splat10", 2D) = "white" {}
        [HideInInspector] _Splat11 ("Splat11", 2D) = "white" {}
        [HideInInspector] _Splat12 ("Splat12", 2D) = "white" {}
        [HideInInspector] _Splat13 ("Splat13", 2D) = "white" {}
        [HideInInspector] _Splat14 ("Splat14", 2D) = "white" {}
        [HideInInspector] _Splat15 ("Splat15", 2D) = "white" {}

        _NumLayers ("Num Layers", Float) = 4
    }

    SubShader
    {
        Tags { "RenderType"="Opaque" "RenderPipeline"="UniversalPipeline" "Queue"="Geometry-100" "TerrainCompatible"="True" }

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode"="UniversalForward" }

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.5

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            TEXTURE2D(_Control0); SAMPLER(sampler_Control0);
            TEXTURE2D(_Control1);
            TEXTURE2D(_Control2);
            TEXTURE2D(_Control3);
            TEXTURE2D(_GlobalMap);

            #define DECL_SPLAT(n) TEXTURE2D(_Splat##n);
            DECL_SPLAT(0) DECL_SPLAT(1) DECL_SPLAT(2) DECL_SPLAT(3)
            DECL_SPLAT(4) DECL_SPLAT(5) DECL_SPLAT(6) DECL_SPLAT(7)
            DECL_SPLAT(8) DECL_SPLAT(9) DECL_SPLAT(10) DECL_SPLAT(11)
            DECL_SPLAT(12) DECL_SPLAT(13) DECL_SPLAT(14) DECL_SPLAT(15)
            SAMPLER(sampler_Splat0);

            CBUFFER_START(UnityPerMaterial)
                float4 _Splat0_ST;  float4 _Splat1_ST;  float4 _Splat2_ST;  float4 _Splat3_ST;
                float4 _Splat4_ST;  float4 _Splat5_ST;  float4 _Splat6_ST;  float4 _Splat7_ST;
                float4 _Splat8_ST;  float4 _Splat9_ST;  float4 _Splat10_ST; float4 _Splat11_ST;
                float4 _Splat12_ST; float4 _Splat13_ST; float4 _Splat14_ST; float4 _Splat15_ST;
                float4 _LayerU[16];
                float4 _LayerV[16];
                float4 _TerrainGlobal; // x=minWorldX, y=minWorldZ, z=sizeX, w=sizeZ
                float4 _GlobalMap_ST;
                float _NumLayers;
                float _UseGlobalMap;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
                float2 uv         : TEXCOORD0;
            };

            struct Varyings
            {
                float4 positionHCS : SV_POSITION;
                float2 uvControl   : TEXCOORD0;
                float3 normalWS    : TEXCOORD1;
                float3 positionWS  : TEXCOORD2;
            };

            Varyings vert (Attributes IN)
            {
                Varyings OUT;
                VertexPositionInputs p = GetVertexPositionInputs(IN.positionOS.xyz);
                OUT.positionHCS = p.positionCS;
                OUT.positionWS  = p.positionWS;
                OUT.normalWS    = TransformObjectToWorldNormal(IN.normalOS);
                OUT.uvControl   = IN.uv;
                return OUT;
            }

            float2 WrapWoTLayerUV(float2 uv)
            {
                // Blender TerrainLayerMappingGroup wraps texture coordinates into
                // [0.0625 .. 0.9375] instead of plain 0..1.  This avoids the 1/16
                // border of WoT terrain tile textures/atlases.
                const float mn = 0.0625;
                const float mx = 0.9375;
                const float span = mx - mn;
                return mn + frac((uv - mn) / span) * span;
            }

            float2 LayerUV(int idx, float3 positionWS)
            {
                // The Blender addon does not use per-chunk local UV for tile
                // textures. It builds world_uv = Generated * chunk_size + chunk_pos,
                // then applies the layer projection. In Unity positionWS.xz is that
                // same continuous world coordinate, so texture patterns now match
                // across chunk borders.
                float3 p = float3(positionWS.x, 0.0, positionWS.z);
                float u = dot(p, _LayerU[idx].xyz) + _LayerU[idx].w;
                float v = dot(p, _LayerV[idx].xyz) + _LayerV[idx].w;
                return WrapWoTLayerUV(float2(u, v));
            }

            float3 DecodeWoTAM(float4 c)
            {
                // Important: do NOT un-premultiply here.  In practice WoT *_AM.dds
                // alpha also carries material data, and dividing RGB by A makes the
                // whole map washed out.  This matches the visual result of using
                // ImageTexture.Color in the Blender addon more closely.
                return c.rgb;
            }

            float3 SampleSplat(int idx, float3 positionWS)
            {
                float2 uv = LayerUV(idx, positionWS);
                #define SAMP(n) if (idx==n) { return DecodeWoTAM(SAMPLE_TEXTURE2D(_Splat##n, sampler_Splat0, uv)); }
                SAMP(0) SAMP(1) SAMP(2) SAMP(3) SAMP(4) SAMP(5) SAMP(6) SAMP(7)
                SAMP(8) SAMP(9) SAMP(10) SAMP(11) SAMP(12) SAMP(13) SAMP(14) SAMP(15)
                return float3(0,0,0);
                #undef SAMP
            }

            half4 frag (Varyings IN) : SV_Target
            {
                float2 uvc = IN.uvControl;

                float4 c0 = SAMPLE_TEXTURE2D(_Control0, sampler_Control0, uvc);
                float4 c1 = SAMPLE_TEXTURE2D(_Control1, sampler_Control0, uvc);
                float4 c2 = SAMPLE_TEXTURE2D(_Control2, sampler_Control0, uvc);
                float4 c3 = SAMPLE_TEXTURE2D(_Control3, sampler_Control0, uvc);
                float w[16];
                w[0]=c0.r; w[1]=c0.g; w[2]=c0.b; w[3]=c0.a;
                w[4]=c1.r; w[5]=c1.g; w[6]=c1.b; w[7]=c1.a;
                w[8]=c2.r; w[9]=c2.g; w[10]=c2.b; w[11]=c2.a;
                w[12]=c3.r; w[13]=c3.g; w[14]=c3.b; w[15]=c3.a;

                int n = (int)_NumLayers;
                float3 albedo = 0;
                float wsum = 0;
                [loop] for (int i = 0; i < 16; i++)
                {
                    if (i >= n) break;
                    albedo += SampleSplat(i, IN.positionWS) * w[i];
                    wsum += w[i];
                }
                // Match Simi4 Blender addon: colors are combined as a raw
                // multiply-add chain, without normalizing the sum of weights.
                // Normalizing makes a tiny overlay mask become a full-coverage
                // Unity terrain layer, which is why chunks looked like one solid
                // texture. If every weight is zero, fall back to layer 0.
                if (wsum <= 1e-4) albedo = SampleSplat(0, IN.positionWS);

                // Optional global AM/tint map from BWT2.settings.global_map_fnv.
                // Blender multiplies terrain base color by this texture when its
                // wetness/global AM option is enabled.  It restores the broad map
                // tint that tile textures alone cannot provide.
                if (_UseGlobalMap > 0.5)
                {
                    float2 guv = (IN.positionWS.xz - _TerrainGlobal.xy) / _TerrainGlobal.zw;
                    guv = guv * _GlobalMap_ST.xy + _GlobalMap_ST.zw;
                    float3 g = SAMPLE_TEXTURE2D(_GlobalMap, sampler_Control0, saturate(guv)).rgb;
                    albedo *= g;
                }

                float3 nWS = normalize(IN.normalWS);
                Light mainLight = GetMainLight();
                float ndotl = saturate(dot(nWS, mainLight.direction));
                float3 color = albedo * (mainLight.color * ndotl + unity_AmbientSky.rgb + 0.2);

                return half4(color, 1);
            }
            ENDHLSL
        }

        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode"="ShadowCaster" }
            ZWrite On ZTest LEqual ColorMask 0
            HLSLPROGRAM
            #pragma vertex shadowVert
            #pragma fragment shadowFrag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            struct A { float4 positionOS : POSITION; };
            struct V { float4 positionHCS : SV_POSITION; };
            V shadowVert (A IN) { V o; o.positionHCS = TransformObjectToHClip(IN.positionOS.xyz); return o; }
            half4 shadowFrag (V IN) : SV_Target { return 0; }
            ENDHLSL
        }
    }
    Fallback "Universal Render Pipeline/Terrain/Lit"
}