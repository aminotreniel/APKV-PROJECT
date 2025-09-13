// Made with Amplify Shader Editor
// Available at the Unity Asset Store - http://u3d.as/y3X 
Shader "Custom/WavingFlag"
{
	Properties
	{
		_Emissivetexture("Emissive texture", 2D) = "white" {}
		[HDR]_Color("Color", Color) = (0,0,0,0)
		_Pulserotationspeed("Pulse rotation speed", Range( 0 , 1)) = 1
		_Pulsespeed("Pulse speed", Range( -2 , 2)) = 0
		_Pulseamplitude("Pulse amplitude", Range( 0 , 15)) = 0
		_Highlightminrange("Highlight min range", Range( 0 , 5)) = 0
		_Highlightmaxrange("Highlight max range", Range( 0 , 5)) = 2.705882
		_Highlightintensity("Highlight intensity", Range( 0 , 20)) = 10
		_Vectorfield("Vector field", 3D) = "white" {}
		_Distortionamplitude("Distortion amplitude", Range( -0.1 , 0.1)) = 0
		[HideInInspector] _texcoord( "", 2D ) = "white" {}
		[HideInInspector] __dirty( "", Int ) = 1
	}

	SubShader
	{
		Tags{ "RenderType" = "Transparent"  "Queue" = "Transparent+0" "IgnoreProjector" = "True" "IsEmissive" = "true"  }
		Cull Off
		CGINCLUDE
		#include "UnityShaderVariables.cginc"
		#include "UnityPBSLighting.cginc"
		#include "Lighting.cginc"
		#pragma target 5.0
		struct Input
		{
			float2 uv_texcoord;
		};

		uniform float4 _Color;
		uniform sampler2D _Emissivetexture;
		uniform float _Distortionamplitude;
		uniform sampler3D _Vectorfield;
		uniform float _Pulserotationspeed;
		uniform float _Pulseamplitude;
		uniform float _Pulsespeed;
		uniform float _Highlightintensity;
		uniform float _Highlightminrange;
		uniform float _Highlightmaxrange;

		void surf( Input i , inout SurfaceOutputStandard o )
		{
			float mulTime51 = _Time.y * _Pulserotationspeed;
			float cos15 = cos( mulTime51 );
			float sin15 = sin( mulTime51 );
			float2 rotator15 = mul( i.uv_texcoord - float2( 0.5,0.5 ) , float2x2( cos15 , -sin15 , sin15 , cos15 )) + float2( 0.5,0.5 );
			float2 break16 = rotator15;
			float temp_output_30_0 = length( ( i.uv_texcoord - float2( 0.5,0.5 ) ) );
			float mulTime13 = _Time.y * _Pulsespeed;
			float3 appendResult14 = (float3(break16.x , ( ( temp_output_30_0 * _Pulseamplitude ) + mulTime13 ) , break16.y));
			float smoothstepResult41 = smoothstep( 0.5 , 0.4 , temp_output_30_0);
			float4 temp_output_9_0 = ( tex3D( _Vectorfield, appendResult14 ) * smoothstepResult41 );
			float4 break22 = ( _Distortionamplitude * temp_output_9_0 );
			float2 appendResult23 = (float2(break22.r , break22.b));
			float4 tex2DNode18 = tex2D( _Emissivetexture, ( i.uv_texcoord + appendResult23 ) );
			float smoothstepResult33 = smoothstep( _Highlightminrange , _Highlightmaxrange , length( temp_output_9_0 ));
			float4 lerpResult36 = lerp( tex2DNode18 , ( tex2DNode18 * _Highlightintensity ) , ( smoothstepResult33 * smoothstepResult41 ));
			o.Emission = ( _Color * lerpResult36 ).rgb;
			float3 temp_cast_1 = (smoothstepResult41).xxx;
			float3 desaturateInitialColor58 = tex2DNode18.rgb;
			float desaturateDot58 = dot( desaturateInitialColor58, float3( 0.299, 0.587, 0.114 ));
			float3 desaturateVar58 = lerp( desaturateInitialColor58, desaturateDot58.xxx, 0.0 );
			float smoothstepResult57 = smoothstep( 0.5 , 0.25 , temp_output_30_0);
			o.Alpha = saturate( ( temp_cast_1 - ( desaturateVar58 * ( 1.0 - smoothstepResult57 ) ) ) ).x;
		}

		ENDCG
		CGPROGRAM
		#pragma surface surf Standard alpha:fade keepalpha fullforwardshadows 

		ENDCG
		Pass
		{
			Name "ShadowCaster"
			Tags{ "LightMode" = "ShadowCaster" }
			ZWrite On
			CGPROGRAM
			#pragma vertex vert
			#pragma fragment frag
			#pragma target 5.0
			#pragma multi_compile_shadowcaster
			#pragma multi_compile UNITY_PASS_SHADOWCASTER
			#pragma skip_variants FOG_LINEAR FOG_EXP FOG_EXP2
			#include "HLSLSupport.cginc"
			#if ( SHADER_API_D3D11 || SHADER_API_GLCORE || SHADER_API_GLES || SHADER_API_GLES3 || SHADER_API_METAL || SHADER_API_VULKAN )
				#define CAN_SKIP_VPOS
			#endif
			#include "UnityCG.cginc"
			#include "Lighting.cginc"
			#include "UnityPBSLighting.cginc"
			sampler3D _DitherMaskLOD;
			struct v2f
			{
				V2F_SHADOW_CASTER;
				float2 customPack1 : TEXCOORD1;
				float3 worldPos : TEXCOORD2;
				UNITY_VERTEX_INPUT_INSTANCE_ID
			};
			v2f vert( appdata_full v )
			{
				v2f o;
				UNITY_SETUP_INSTANCE_ID( v );
				UNITY_INITIALIZE_OUTPUT( v2f, o );
				UNITY_TRANSFER_INSTANCE_ID( v, o );
				Input customInputData;
				float3 worldPos = mul( unity_ObjectToWorld, v.vertex ).xyz;
				half3 worldNormal = UnityObjectToWorldNormal( v.normal );
				o.customPack1.xy = customInputData.uv_texcoord;
				o.customPack1.xy = v.texcoord;
				o.worldPos = worldPos;
				TRANSFER_SHADOW_CASTER_NORMALOFFSET( o )
				return o;
			}
			half4 frag( v2f IN
			#if !defined( CAN_SKIP_VPOS )
			, UNITY_VPOS_TYPE vpos : VPOS
			#endif
			) : SV_Target
			{
				UNITY_SETUP_INSTANCE_ID( IN );
				Input surfIN;
				UNITY_INITIALIZE_OUTPUT( Input, surfIN );
				surfIN.uv_texcoord = IN.customPack1.xy;
				float3 worldPos = IN.worldPos;
				half3 worldViewDir = normalize( UnityWorldSpaceViewDir( worldPos ) );
				SurfaceOutputStandard o;
				UNITY_INITIALIZE_OUTPUT( SurfaceOutputStandard, o )
				surf( surfIN, o );
				#if defined( CAN_SKIP_VPOS )
				float2 vpos = IN.pos;
				#endif
				half alphaRef = tex3D( _DitherMaskLOD, float3( vpos.xy * 0.25, o.Alpha * 0.9375 ) ).a;
				clip( alphaRef - 0.01 );
				SHADOW_CASTER_FRAGMENT( IN )
			}
			ENDCG
		}
	}
	Fallback "Diffuse"
	CustomEditor "ASEMaterialInspector"
}
/*ASEBEGIN
Version=16600
1976;8;1857;1133;768.8713;586.6988;1.3;True;False
Node;AmplifyShaderEditor.CommentaryNode;59;-4156.459,191.912;Float;False;1998.007;634.9421;Vector field access;16;24;13;26;27;43;9;41;11;14;16;15;51;30;28;45;12;;1,1,1,1;0;0
Node;AmplifyShaderEditor.TextureCoordinatesNode;12;-4106.46,269.9541;Float;False;0;-1;2;3;2;SAMPLER2D;;False;0;FLOAT2;1,1;False;1;FLOAT2;0,0;False;5;FLOAT2;0;FLOAT;1;FLOAT;2;FLOAT;3;FLOAT;4
Node;AmplifyShaderEditor.RangedFloatNode;45;-3776.724,345.4272;Float;False;Property;_Pulserotationspeed;Pulse rotation speed;2;0;Create;True;0;0;False;0;1;0.2;0;1;0;1;FLOAT;0
Node;AmplifyShaderEditor.SimpleSubtractOpNode;28;-3798.041,453.9972;Float;False;2;0;FLOAT2;0,0;False;1;FLOAT2;0.5,0.5;False;1;FLOAT2;0
Node;AmplifyShaderEditor.RangedFloatNode;43;-3748.278,609.3308;Float;False;Property;_Pulseamplitude;Pulse amplitude;4;0;Create;True;0;0;False;0;0;3.64;0;15;0;1;FLOAT;0
Node;AmplifyShaderEditor.RangedFloatNode;27;-3747.244,696.0464;Float;False;Property;_Pulsespeed;Pulse speed;3;0;Create;True;0;0;False;0;0;-0.75;-2;2;0;1;FLOAT;0
Node;AmplifyShaderEditor.LengthOpNode;30;-3634.041,461.9972;Float;False;1;0;FLOAT2;0,0;False;1;FLOAT;0
Node;AmplifyShaderEditor.SimpleTimeNode;51;-3480.936,339.9924;Float;False;1;0;FLOAT;1;False;1;FLOAT;0
Node;AmplifyShaderEditor.SimpleTimeNode;13;-3420.361,701.1544;Float;False;1;0;FLOAT;0.25;False;1;FLOAT;0
Node;AmplifyShaderEditor.SimpleMultiplyOpNode;26;-3418.631,591.6411;Float;False;2;2;0;FLOAT;0;False;1;FLOAT;0;False;1;FLOAT;0
Node;AmplifyShaderEditor.RotatorNode;15;-3277.28,264.2168;Float;False;3;0;FLOAT2;0,0;False;1;FLOAT2;0.5,0.5;False;2;FLOAT;0.5;False;1;FLOAT2;0
Node;AmplifyShaderEditor.SimpleAddOpNode;24;-3218.799,627.5734;Float;False;2;2;0;FLOAT;0;False;1;FLOAT;0;False;1;FLOAT;0
Node;AmplifyShaderEditor.BreakToComponentsNode;16;-3058.28,261.2167;Float;False;FLOAT2;1;0;FLOAT2;0,0;False;16;FLOAT;0;FLOAT;1;FLOAT;2;FLOAT;3;FLOAT;4;FLOAT;5;FLOAT;6;FLOAT;7;FLOAT;8;FLOAT;9;FLOAT;10;FLOAT;11;FLOAT;12;FLOAT;13;FLOAT;14;FLOAT;15
Node;AmplifyShaderEditor.DynamicAppendNode;14;-2790.861,248.58;Float;False;FLOAT3;4;0;FLOAT;0;False;1;FLOAT;0;False;2;FLOAT;0;False;3;FLOAT;0;False;1;FLOAT3;0
Node;AmplifyShaderEditor.SamplerNode;11;-2624.244,241.912;Float;True;Property;_Vectorfield;Vector field;8;0;Create;True;0;0;False;0;None;c70e6ea7ce4c25d46a2a8c17675ecd05;True;0;False;white;LockedToTexture3D;False;Object;-1;Auto;Texture3D;6;0;SAMPLER2D;;False;1;FLOAT3;0,0,0;False;2;FLOAT;0;False;3;FLOAT3;0,0,0;False;4;FLOAT3;0,0,0;False;5;FLOAT;1;False;5;COLOR;0;FLOAT;1;FLOAT;2;FLOAT;3;FLOAT;4
Node;AmplifyShaderEditor.SmoothstepOpNode;41;-2522.619,473.8661;Float;False;3;0;FLOAT;0;False;1;FLOAT;0.5;False;2;FLOAT;0.4;False;1;FLOAT;0
Node;AmplifyShaderEditor.CommentaryNode;61;-2150.216,-402.2184;Float;False;1466.345;456.4043;Main texture access;7;10;44;22;23;20;21;18;;1,1,1,1;0;0
Node;AmplifyShaderEditor.RangedFloatNode;10;-2100.216,-123.2971;Float;False;Property;_Distortionamplitude;Distortion amplitude;9;0;Create;True;0;0;False;0;0;-0.0482353;-0.1;0.1;0;1;FLOAT;0
Node;AmplifyShaderEditor.SimpleMultiplyOpNode;9;-2300.154,273.0844;Float;False;2;2;0;COLOR;0,0,0,0;False;1;FLOAT;0;False;1;COLOR;0
Node;AmplifyShaderEditor.SimpleMultiplyOpNode;44;-1797.904,-124.365;Float;False;2;2;0;FLOAT;0;False;1;COLOR;0,0,0,0;False;1;COLOR;0
Node;AmplifyShaderEditor.BreakToComponentsNode;22;-1628.831,-124.8141;Float;False;COLOR;1;0;COLOR;0,0,0,0;False;16;FLOAT;0;FLOAT;1;FLOAT;2;FLOAT;3;FLOAT;4;FLOAT;5;FLOAT;6;FLOAT;7;FLOAT;8;FLOAT;9;FLOAT;10;FLOAT;11;FLOAT;12;FLOAT;13;FLOAT;14;FLOAT;15
Node;AmplifyShaderEditor.TextureCoordinatesNode;20;-1856.706,-352.2184;Float;False;0;-1;2;3;2;SAMPLER2D;;False;0;FLOAT2;1,1;False;1;FLOAT2;0,0;False;5;FLOAT2;0;FLOAT;1;FLOAT;2;FLOAT;3;FLOAT;4
Node;AmplifyShaderEditor.DynamicAppendNode;23;-1372.516,-120.2861;Float;False;FLOAT2;4;0;FLOAT;0;False;1;FLOAT;0;False;2;FLOAT;0;False;3;FLOAT;0;False;1;FLOAT2;0
Node;AmplifyShaderEditor.CommentaryNode;60;-1124.188,139.8243;Float;False;1409.904;468.4713;Highlight;8;47;32;49;33;36;46;37;50;;1,1,1,1;0;0
Node;AmplifyShaderEditor.SimpleAddOpNode;21;-1206.178,-193.9423;Float;False;2;2;0;FLOAT2;0,0;False;1;FLOAT2;0,0;False;1;FLOAT2;0
Node;AmplifyShaderEditor.CommentaryNode;62;-849.8229,695.5878;Float;False;1133.713;308.9589;Cutout;6;55;56;52;53;58;57;;1,1,1,1;0;0
Node;AmplifyShaderEditor.SmoothstepOpNode;57;-799.8229,848.5468;Float;False;3;0;FLOAT;0;False;1;FLOAT;0.5;False;2;FLOAT;0.25;False;1;FLOAT;0
Node;AmplifyShaderEditor.LengthOpNode;32;-913.0991,310.252;Float;False;1;0;COLOR;0,0,0,0;False;1;FLOAT;0
Node;AmplifyShaderEditor.RangedFloatNode;47;-1073.188,391.2493;Float;False;Property;_Highlightminrange;Highlight min range;5;0;Create;True;0;0;False;0;0;1.18;0;5;0;1;FLOAT;0
Node;AmplifyShaderEditor.RangedFloatNode;49;-1074.188,467.2493;Float;False;Property;_Highlightmaxrange;Highlight max range;6;0;Create;True;0;0;False;0;2.705882;3.235294;0;5;0;1;FLOAT;0
Node;AmplifyShaderEditor.SamplerNode;18;-1004.871,-178.4347;Float;True;Property;_Emissivetexture;Emissive texture;0;0;Create;True;0;0;False;0;None;f7e96904e8667e1439548f0f86389447;True;0;False;white;Auto;False;Object;-1;Auto;Texture2D;6;0;SAMPLER2D;;False;1;FLOAT2;0,0;False;2;FLOAT;0;False;3;FLOAT2;0,0;False;4;FLOAT2;0,0;False;5;FLOAT;1;False;5;COLOR;0;FLOAT;1;FLOAT;2;FLOAT;3;FLOAT;4
Node;AmplifyShaderEditor.SmoothstepOpNode;33;-769.1238,309.1607;Float;False;3;0;FLOAT;0;False;1;FLOAT;1;False;2;FLOAT;5;False;1;FLOAT;0
Node;AmplifyShaderEditor.RangedFloatNode;46;-520.588,210.8493;Float;False;Property;_Highlightintensity;Highlight intensity;7;0;Create;True;0;0;False;0;10;20;0;20;0;1;FLOAT;0
Node;AmplifyShaderEditor.DesaturateOpNode;58;-582.5541,745.5878;Float;False;2;0;FLOAT3;0,0,0;False;1;FLOAT;0;False;1;FLOAT3;0
Node;AmplifyShaderEditor.OneMinusNode;52;-570.4954,854.031;Float;False;1;0;FLOAT;0;False;1;FLOAT;0
Node;AmplifyShaderEditor.SimpleMultiplyOpNode;50;-481.9514,475.2955;Float;False;2;2;0;FLOAT;0;False;1;FLOAT;0;False;1;FLOAT;0
Node;AmplifyShaderEditor.SimpleMultiplyOpNode;37;-190.2287,189.8243;Float;False;2;2;0;COLOR;0,0,0,0;False;1;FLOAT;0;False;1;COLOR;0
Node;AmplifyShaderEditor.SimpleMultiplyOpNode;53;-329.0869,832.8409;Float;False;2;2;0;FLOAT3;0,0,0;False;1;FLOAT;0;False;1;FLOAT3;0
Node;AmplifyShaderEditor.CommentaryNode;65;4.10498,-227.3535;Float;False;548;306;Recoloring;2;64;63;;1,1,1,1;0;0
Node;AmplifyShaderEditor.ColorNode;64;55.10498,-178.3535;Float;False;Property;_Color;Color;1;1;[HDR];Create;True;0;0;False;0;0,0,0,0;0,2.297397,1.833443,0;True;0;5;COLOR;0;FLOAT;1;FLOAT;2;FLOAT;3;FLOAT;4
Node;AmplifyShaderEditor.LerpOp;36;101.7163,211.1602;Float;False;3;0;COLOR;0,0,0,0;False;1;COLOR;0,0,0,0;False;2;FLOAT;0;False;1;COLOR;0
Node;AmplifyShaderEditor.SimpleSubtractOpNode;55;-72.31961,777.6029;Float;False;2;0;FLOAT;0;False;1;FLOAT3;0,0,0;False;1;FLOAT3;0
Node;AmplifyShaderEditor.SimpleMultiplyOpNode;63;383.105,-54.35352;Float;False;2;2;0;COLOR;0,0,0,0;False;1;COLOR;0,0,0,0;False;1;COLOR;0
Node;AmplifyShaderEditor.SaturateNode;56;108.8905,810.8021;Float;False;1;0;FLOAT3;0,0,0;False;1;FLOAT3;0
Node;AmplifyShaderEditor.StandardSurfaceOutputNode;0;656.8541,280.837;Float;False;True;7;Float;ASEMaterialInspector;0;0;Standard;Custom/WavingFlag;False;False;False;False;False;False;False;False;False;False;False;False;False;False;True;False;False;False;False;False;False;Off;0;False;-1;0;False;-1;False;0;False;-1;0;False;-1;False;0;Transparent;0.5;True;True;0;False;Transparent;;Transparent;All;True;True;True;True;True;True;True;True;True;True;True;True;True;True;True;True;True;0;False;-1;False;0;False;-1;255;False;-1;255;False;-1;0;False;-1;0;False;-1;0;False;-1;0;False;-1;0;False;-1;0;False;-1;0;False;-1;0;False;-1;False;2;33.4;10;25;False;5;True;2;5;False;-1;10;False;-1;0;0;False;-1;0;False;-1;0;False;-1;0;False;-1;0;False;0;0,0,0,0;VertexOffset;True;False;Cylindrical;False;Relative;0;;-1;-1;-1;-1;0;False;0;0;False;-1;-1;0;False;-1;0;0;0;False;1;False;-1;0;False;-1;16;0;FLOAT3;0,0,0;False;1;FLOAT3;0,0,0;False;2;FLOAT3;0,0,0;False;3;FLOAT;0;False;4;FLOAT;0;False;5;FLOAT;0;False;6;FLOAT3;0,0,0;False;7;FLOAT3;0,0,0;False;8;FLOAT;0;False;9;FLOAT;0;False;10;FLOAT;0;False;13;FLOAT3;0,0,0;False;11;FLOAT3;0,0,0;False;12;FLOAT3;0,0,0;False;14;FLOAT4;0,0,0,0;False;15;FLOAT3;0,0,0;False;0
WireConnection;28;0;12;0
WireConnection;30;0;28;0
WireConnection;51;0;45;0
WireConnection;13;0;27;0
WireConnection;26;0;30;0
WireConnection;26;1;43;0
WireConnection;15;0;12;0
WireConnection;15;2;51;0
WireConnection;24;0;26;0
WireConnection;24;1;13;0
WireConnection;16;0;15;0
WireConnection;14;0;16;0
WireConnection;14;1;24;0
WireConnection;14;2;16;1
WireConnection;11;1;14;0
WireConnection;41;0;30;0
WireConnection;9;0;11;0
WireConnection;9;1;41;0
WireConnection;44;0;10;0
WireConnection;44;1;9;0
WireConnection;22;0;44;0
WireConnection;23;0;22;0
WireConnection;23;1;22;2
WireConnection;21;0;20;0
WireConnection;21;1;23;0
WireConnection;57;0;30;0
WireConnection;32;0;9;0
WireConnection;18;1;21;0
WireConnection;33;0;32;0
WireConnection;33;1;47;0
WireConnection;33;2;49;0
WireConnection;58;0;18;0
WireConnection;52;0;57;0
WireConnection;50;0;33;0
WireConnection;50;1;41;0
WireConnection;37;0;18;0
WireConnection;37;1;46;0
WireConnection;53;0;58;0
WireConnection;53;1;52;0
WireConnection;36;0;18;0
WireConnection;36;1;37;0
WireConnection;36;2;50;0
WireConnection;55;0;41;0
WireConnection;55;1;53;0
WireConnection;63;0;64;0
WireConnection;63;1;36;0
WireConnection;56;0;55;0
WireConnection;0;2;63;0
WireConnection;0;9;56;0
ASEEND*/
//CHKSM=C28F65FB6B7F9B6793F32AB7F8764FE3AC302F77