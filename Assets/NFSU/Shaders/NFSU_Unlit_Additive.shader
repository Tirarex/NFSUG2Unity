// Additive variant: lights, glow sprites, lens-style decals. Fog attenuates towards
// black instead of the fog colour, which is how D3D handled additive fog in the game.
Shader "NFSU/Unlit VertexColor Additive"
{
    Properties
    {
        [MainTexture] _MainTex("Base Texture", 2D) = "white" {}
        [HDR]_TintColor("Tint", Color) = (1,1,1,1)
    }

    SubShader
    {
        Tags { "Queue"="Transparent" "RenderType"="Transparent" "IgnoreProjector"="True" }
        LOD 100

        Pass
        {
            Cull Off
            ZWrite Off
            ZTest LEqual
            Blend SrcAlpha One

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
                // Fog for an additive pass has to fade towards BLACK, not towards the scene fog
                // colour (an additive blend adds the fog colour to the frame everywhere). Use the
                // Unity macro, not i.fogCoord directly: with all fog keywords disabled
                // UNITY_FOG_COORDS() expands to nothing, so a bare i.fogCoord does not exist and
                // the <no keywords> variant fails to compile ("invalid subscript 'fogCoord'").
                UNITY_APPLY_FOG_COLOR(i.fogCoord, c, fixed4(0, 0, 0, 0));
                return c;
            }
            ENDCG
        }
    }

    Fallback "NFSU/Unlit VertexColor"
}
