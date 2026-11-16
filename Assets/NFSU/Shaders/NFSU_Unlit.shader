// Unlit + vertex colour shader used by the NFSU map importer.
//
// NFSU scenery stores no normals at all: the 24-byte vertex record is
// position + prelight D3DCOLOR (BGRA) + uv. The game therefore looks unlit with baked
// vertex lighting, and the Collada export from AssetDumper is the same. These shaders
// reproduce that: base texture * vertex colour, no lighting, distance fog.
Shader "NFSU/Unlit VertexColor"
{
    Properties
    {
        [MainTexture] _MainTex("Base Texture", 2D) = "white" {}
        _TintColor("Tint", Color) = (1,1,1,1)
    }

    SubShader
    {
        Tags { "Queue"="Geometry" "RenderType"="Opaque" "IgnoreProjector"="True" }
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
                UNITY_APPLY_FOG(i.fogCoord, c);
                return c;
            }
            ENDCG
        }
    }

    Fallback "Unlit/Texture"
}
