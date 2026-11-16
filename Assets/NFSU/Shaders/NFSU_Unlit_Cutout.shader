// Alpha-tested variant: for DXT3/DXT5 textures that carry real transparency
// (fences, chain-link barriers, foliage, grilles). Still fully unlit + vertex colour.
Shader "NFSU/Unlit VertexColor Cutout"
{
    Properties
    {
        [MainTexture] _MainTex("Base Texture", 2D) = "white" {}
        _TintColor("Tint", Color) = (1,1,1,1)
        _Cutoff("Alpha Cutoff", Range(0,1)) = 0.5
    }

    SubShader
    {
        Tags { "Queue"="AlphaTest" "RenderType"="TransparentCutout" "IgnoreProjector"="True" }
        LOD 100

        Pass
        {
            Cull Off 
            ZTest LEqual

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_fog
            #include "UnityCG.cginc"

            sampler2D _MainTex;
            float4 _MainTex_ST;
            fixed4 _TintColor;
            float _Cutoff;

            struct appdata
            {
                float4 position : POSITION;
                float2 uv : TEXCOORD0;
                fixed4 color : COLOR;
            };

            struct v2f
            {
                float4 vertex : SV_POSITION;
                float2 uv : TEXCOORD0;
                fixed4 color : COLOR;
                UNITY_FOG_COORDS(1)
            };

            v2f vert(appdata v)
            {
                v2f o;
                o.vertex = UnityObjectToClipPos(v.position);
                o.uv = TRANSFORM_TEX(v.uv, _MainTex);
                o.color = v.color * _TintColor;
                UNITY_TRANSFER_FOG(o, o.vertex);
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                fixed4 c = tex2D(_MainTex, i.uv) * i.color;
                clip(c.a - _Cutoff);
                UNITY_APPLY_FOG(i.fogCoord, c);
                return fixed4(c.rgb, 1);
            }
            ENDCG
        }
    }

    Fallback "NFSU/Unlit VertexColor"
}
