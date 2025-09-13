// Made with Amplify Shader Editor
// Available at the Unity Asset Store - http://u3d.as/y3X 
Shader "Custom/MeteorTrail"
{
	Properties
	{
		_TextureSample0("Texture Sample 0", 2D) = "white" {}
		_TextureSample1("Texture Sample 1", 3D) = "white" {}
		_Vectorfieldfrequency("Vector field frequency", Range( 0.1 , 1)) = 0
		_Amplitude("Amplitude", Range( 0 , 1)) = 0
		_Rotationspeed("Rotation speed", Range( -10 , 10)) = 0
		_Twistintensity("Twist intensity", Range( -2 , 2)) = 0
		[HDR]_Head("Head", Color) = (0,0,0,0)
		[HDR]_Tail("Tail", Color) = (0.2006095,0,0,0)
		[HDR]_Innercolor("Inner color", Color) = (0,0,0,0)
		[HDR]_Outercolor("Outer color", Color) = (0,0,0,0)
		_HeadTailramp("Head-Tail ramp", Range( 0.1 , 3)) = 0
	}
	
	SubShader
	{
		Tags { "RenderType"="TransparentCutout" }
		
		Pass
		{
			
			Name "First"
			CGINCLUDE
			#pragma target 5.0
			ENDCG
			Blend Off
			Cull Front
			ColorMask RGBA
			ZWrite On
			ZTest LEqual
			Offset 0 , 0
			
			CGPROGRAM
			#pragma vertex vert
			#pragma fragment frag
			#include "UnityCG.cginc"
			#include "UnityShaderVariables.cginc"


			struct appdata
			{
				float4 vertex : POSITION;
				UNITY_VERTEX_INPUT_INSTANCE_ID
				float4 ase_texcoord : TEXCOORD0;
			};
			
			struct v2f
			{
				float4 vertex : SV_POSITION;
				UNITY_VERTEX_OUTPUT_STEREO
				float4 ase_texcoord : TEXCOORD0;
				float4 ase_texcoord1 : TEXCOORD1;
			};

			uniform sampler3D _TextureSample1;
			uniform float _Rotationspeed;
			uniform float _Twistintensity;
			uniform float _Vectorfieldfrequency;
			uniform float _Amplitude;
			uniform float4 _Outercolor;
			uniform float4 _Innercolor;
			uniform sampler2D _TextureSample0;
			uniform float4 _Head;
			uniform float4 _Tail;
			uniform float _HeadTailramp;
			float3 RotateAroundAxis( float3 center, float3 original, float3 u, float angle )
			{
				original -= center;
				float C = cos( angle );
				float S = sin( angle );
				float t = 1 - C;
				float m00 = t * u.x * u.x + C;
				float m01 = t * u.x * u.y - S * u.z;
				float m02 = t * u.x * u.z + S * u.y;
				float m10 = t * u.x * u.y + S * u.z;
				float m11 = t * u.y * u.y + C;
				float m12 = t * u.y * u.z - S * u.x;
				float m20 = t * u.x * u.z - S * u.y;
				float m21 = t * u.y * u.z + S * u.x;
				float m22 = t * u.z * u.z + C;
				float3x3 finalMatrix = float3x3( m00, m01, m02, m10, m11, m12, m20, m21, m22 );
				return mul( finalMatrix, original ) + center;
			}
			
			
			v2f vert ( appdata v )
			{
				v2f o;
				UNITY_SETUP_INSTANCE_ID(v);
				UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
				float3 appendResult12 = (float3(0.0 , 0.0 , _Time.y));
				float mulTime21 = _Time.y * _Rotationspeed;
				float3 rotatedValue19 = RotateAroundAxis( float3( 0,0,0 ), v.vertex.xyz, float3(0,0,1), ( mulTime21 + ( _Twistintensity * v.vertex.xyz.z ) ) );
				float smoothstepResult17 = smoothstep( 5.0 , -3.0 , v.vertex.xyz.z);
				float4 temp_output_16_0 = ( tex3Dlod( _TextureSample1, float4( ( appendResult12 + ( rotatedValue19 * _Vectorfieldfrequency ) ), 0.0) ) * _Amplitude * smoothstepResult17 );
				
				o.ase_texcoord = v.vertex;
				o.ase_texcoord1.xyz = v.ase_texcoord.xyz;
				
				//setting value to unused interpolator channels and avoid initialization warnings
				o.ase_texcoord1.w = 0;
				
				v.vertex.xyz += temp_output_16_0.rgb;
				o.vertex = UnityObjectToClipPos(v.vertex);
				return o;
			}
			
			fixed4 frag (v2f i ) : SV_Target
			{
				fixed4 finalColor;
				float smoothstepResult27 = smoothstep( 5.0 , -5.0 , i.ase_texcoord.xyz.z);
				float2 uv029 = i.ase_texcoord1.xyz * float2( 1,1 ) + float2( 0,0 );
				float2 panner28 = ( 1.0 * _Time.y * float2( 0,1 ) + uv029);
				float4 tex2DNode2 = tex2D( _TextureSample0, panner28 );
				float smoothstepResult34 = smoothstep( smoothstepResult27 , 1.0 , tex2DNode2.g);
				float4 lerpResult35 = lerp( _Outercolor , _Innercolor , smoothstepResult34);
				clip( tex2DNode2.g - smoothstepResult27);
				float smoothstepResult17 = smoothstep( 5.0 , -3.0 , i.ase_texcoord.xyz.z);
				float4 lerpResult32 = lerp( _Head , _Tail , pow( smoothstepResult17 , _HeadTailramp ));
				float4 temp_output_33_0 = ( lerpResult35 * lerpResult32 );
				
				
				finalColor = temp_output_33_0;
				return finalColor;
			}
			ENDCG
		}

		
		Pass
		{
			Name "Second"
			
			CGINCLUDE
			#pragma target 5.0
			ENDCG
			Blend Off
			Cull Back
			ColorMask RGBA
			ZWrite On
			ZTest LEqual
			Offset 0 , 0
			
			CGPROGRAM
			#pragma vertex vert
			#pragma fragment frag
			#include "UnityCG.cginc"
			#include "UnityShaderVariables.cginc"


			struct appdata
			{
				float4 vertex : POSITION;
				UNITY_VERTEX_INPUT_INSTANCE_ID
				float4 ase_texcoord : TEXCOORD0;
			};
			
			struct v2f
			{
				float4 vertex : SV_POSITION;
				UNITY_VERTEX_OUTPUT_STEREO
				float4 ase_texcoord : TEXCOORD0;
				float4 ase_texcoord1 : TEXCOORD1;
			};

			uniform sampler3D _TextureSample1;
			uniform float _Rotationspeed;
			uniform float _Twistintensity;
			uniform float _Vectorfieldfrequency;
			uniform float _Amplitude;
			uniform float4 _Outercolor;
			uniform float4 _Innercolor;
			uniform sampler2D _TextureSample0;
			uniform float4 _Head;
			uniform float4 _Tail;
			uniform float _HeadTailramp;
			float3 RotateAroundAxis( float3 center, float3 original, float3 u, float angle )
			{
				original -= center;
				float C = cos( angle );
				float S = sin( angle );
				float t = 1 - C;
				float m00 = t * u.x * u.x + C;
				float m01 = t * u.x * u.y - S * u.z;
				float m02 = t * u.x * u.z + S * u.y;
				float m10 = t * u.x * u.y + S * u.z;
				float m11 = t * u.y * u.y + C;
				float m12 = t * u.y * u.z - S * u.x;
				float m20 = t * u.x * u.z - S * u.y;
				float m21 = t * u.y * u.z + S * u.x;
				float m22 = t * u.z * u.z + C;
				float3x3 finalMatrix = float3x3( m00, m01, m02, m10, m11, m12, m20, m21, m22 );
				return mul( finalMatrix, original ) + center;
			}
			
			
			v2f vert ( appdata v )
			{
				v2f o;
				UNITY_SETUP_INSTANCE_ID(v);
				UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
				float3 appendResult12 = (float3(0.0 , 0.0 , _Time.y));
				float mulTime21 = _Time.y * _Rotationspeed;
				float3 rotatedValue19 = RotateAroundAxis( float3( 0,0,0 ), v.vertex.xyz, float3(0,0,1), ( mulTime21 + ( _Twistintensity * v.vertex.xyz.z ) ) );
				float smoothstepResult17 = smoothstep( 5.0 , -3.0 , v.vertex.xyz.z);
				float4 temp_output_16_0 = ( tex3Dlod( _TextureSample1, float4( ( appendResult12 + ( rotatedValue19 * _Vectorfieldfrequency ) ), 0.0) ) * _Amplitude * smoothstepResult17 );
				
				o.ase_texcoord = v.vertex;
				o.ase_texcoord1.xyz = v.ase_texcoord.xyz;
				
				//setting value to unused interpolator channels and avoid initialization warnings
				o.ase_texcoord1.w = 0;
				
				v.vertex.xyz += temp_output_16_0.rgb;
				o.vertex = UnityObjectToClipPos(v.vertex);
				return o;
			}
			
			fixed4 frag (v2f i ) : SV_Target
			{
				fixed4 finalColor;
				float smoothstepResult27 = smoothstep( 5.0 , -5.0 , i.ase_texcoord.xyz.z);
				float2 uv029 = i.ase_texcoord1.xyz * float2( 1,1 ) + float2( 0,0 );
				float2 panner28 = ( 1.0 * _Time.y * float2( 0,1 ) + uv029);
				float4 tex2DNode2 = tex2D( _TextureSample0, panner28 );
				float smoothstepResult34 = smoothstep( smoothstepResult27 , 1.0 , tex2DNode2.g);
				float4 lerpResult35 = lerp( _Outercolor , _Innercolor , smoothstepResult34);
				clip( tex2DNode2.g - smoothstepResult27);
				float smoothstepResult17 = smoothstep( 5.0 , -3.0 , i.ase_texcoord.xyz.z);
				float4 lerpResult32 = lerp( _Head , _Tail , pow( smoothstepResult17 , _HeadTailramp ));
				float4 temp_output_33_0 = ( lerpResult35 * lerpResult32 );
				
				
				finalColor = temp_output_33_0;
				return finalColor;
			}
			ENDCG
		}
	}
	CustomEditor "ASEMaterialInspector"
	
	
}
/*ASEBEGIN
Version=16600
1976;8;1857;1133;593.1345;-136.4844;1;True;False
Node;AmplifyShaderEditor.CommentaryNode;42;-2404.476,-22.68557;Float;False;2307.804;719.4475;Vector field access;14;23;21;13;12;14;15;10;8;24;25;22;19;20;9;;1,1,1,1;0;0
Node;AmplifyShaderEditor.CommentaryNode;41;-635.9705,-1151.072;Float;False;1874.18;987.1393;Main body coloring and cliping;9;27;2;28;29;26;35;36;34;37;;1,1,1,1;0;0
Node;AmplifyShaderEditor.PosVertexDataNode;9;-2242.406,517.762;Float;False;0;0;5;FLOAT3;0;FLOAT;1;FLOAT;2;FLOAT;3;FLOAT;4
Node;AmplifyShaderEditor.RangedFloatNode;24;-2332.635,353.7997;Float;False;Property;_Twistintensity;Twist intensity;5;0;Create;True;0;0;False;0;0;0.8;-2;2;0;1;FLOAT;0
Node;AmplifyShaderEditor.RangedFloatNode;23;-2354.476,253.1114;Float;False;Property;_Rotationspeed;Rotation speed;4;0;Create;True;0;0;False;0;0;-0.75;-10;10;0;1;FLOAT;0
Node;AmplifyShaderEditor.SimpleMultiplyOpNode;25;-2006.43,354.8549;Float;False;2;2;0;FLOAT;0;False;1;FLOAT;0;False;1;FLOAT;0
Node;AmplifyShaderEditor.SimpleTimeNode;21;-2016.302,237.7478;Float;False;1;0;FLOAT;1;False;1;FLOAT;0
Node;AmplifyShaderEditor.TextureCoordinatesNode;29;-580.4867,-651.0493;Float;False;0;-1;2;3;2;SAMPLER2D;;False;0;FLOAT2;1,1;False;1;FLOAT2;0,0;False;5;FLOAT2;0;FLOAT;1;FLOAT;2;FLOAT;3;FLOAT;4
Node;AmplifyShaderEditor.Vector3Node;20;-1901.505,27.31443;Float;False;Constant;_Vector0;Vector 0;4;0;Create;True;0;0;False;0;0,0,1;0,0,0;0;4;FLOAT3;0;FLOAT;1;FLOAT;2;FLOAT;3
Node;AmplifyShaderEditor.PannerNode;28;-318.1727,-657.532;Float;False;3;0;FLOAT2;0,0;False;2;FLOAT2;0,1;False;1;FLOAT;1;False;1;FLOAT2;0
Node;AmplifyShaderEditor.SimpleAddOpNode;22;-1777.657,264.8227;Float;False;2;2;0;FLOAT;0;False;1;FLOAT;0;False;1;FLOAT;0
Node;AmplifyShaderEditor.CommentaryNode;43;54.3226,222.0987;Float;False;587.3195;354.7696;Vector field distortion;3;16;18;17;;1,1,1,1;0;0
Node;AmplifyShaderEditor.RotateAboutAxisNode;19;-1603.998,180.1204;Float;False;False;4;0;FLOAT3;0,0,0;False;1;FLOAT;0;False;2;FLOAT3;0,0,0;False;3;FLOAT3;0,0,0;False;1;FLOAT3;0
Node;AmplifyShaderEditor.RangedFloatNode;15;-1264.168,358.9901;Float;False;Property;_Vectorfieldfrequency;Vector field frequency;2;0;Create;True;0;0;False;0;0;0.251;0.1;1;0;1;FLOAT;0
Node;AmplifyShaderEditor.SmoothstepOpNode;27;-63.93471,-444.3515;Float;True;3;0;FLOAT;0;False;1;FLOAT;5;False;2;FLOAT;-5;False;1;FLOAT;0
Node;AmplifyShaderEditor.SimpleTimeNode;13;-1206.529,36.31414;Float;False;1;0;FLOAT;1;False;1;FLOAT;0
Node;AmplifyShaderEditor.CommentaryNode;40;78.60894,804.8525;Float;False;719.2748;636.2278;Head-Tail coloring;5;39;38;31;30;32;;1,1,1,1;0;0
Node;AmplifyShaderEditor.SamplerNode;2;-95.50672,-685.6729;Float;True;Property;_TextureSample0;Texture Sample 0;0;0;Create;True;0;0;False;0;e28dc97a9541e3642a48c0e3886688c5;e28dc97a9541e3642a48c0e3886688c5;True;0;False;white;Auto;False;Object;-1;Auto;Texture2D;6;0;SAMPLER2D;;False;1;FLOAT2;0,0;False;2;FLOAT;0;False;3;FLOAT2;0,0;False;4;FLOAT2;0,0;False;5;FLOAT;1;False;5;COLOR;0;FLOAT;1;FLOAT;2;FLOAT;3;FLOAT;4
Node;AmplifyShaderEditor.ColorNode;37;315.6925,-1042.58;Float;False;Property;_Outercolor;Outer color;9;1;[HDR];Create;True;0;0;False;0;0,0,0,0;0.7735849,0.1976371,0,0;True;0;5;COLOR;0;FLOAT;1;FLOAT;2;FLOAT;3;FLOAT;4
Node;AmplifyShaderEditor.DynamicAppendNode;12;-905.7407,37.40974;Float;False;FLOAT3;4;0;FLOAT;0;False;1;FLOAT;0;False;2;FLOAT;0;False;3;FLOAT;0;False;1;FLOAT3;0
Node;AmplifyShaderEditor.ColorNode;36;300.657,-862.457;Float;False;Property;_Innercolor;Inner color;8;1;[HDR];Create;True;0;0;False;0;0,0,0,0;7.569376,7.569376,7.569376,0;True;0;5;COLOR;0;FLOAT;1;FLOAT;2;FLOAT;3;FLOAT;4
Node;AmplifyShaderEditor.SimpleMultiplyOpNode;14;-900.8241,204.0607;Float;False;2;2;0;FLOAT3;0,0,0;False;1;FLOAT;0;False;1;FLOAT3;0
Node;AmplifyShaderEditor.SmoothstepOpNode;17;104.3226,422.1683;Float;False;3;0;FLOAT;0;False;1;FLOAT;5;False;2;FLOAT;-3;False;1;FLOAT;0
Node;AmplifyShaderEditor.SmoothstepOpNode;34;305.0607,-684.0407;Float;True;3;0;FLOAT;0;False;1;FLOAT;0;False;2;FLOAT;1;False;1;FLOAT;0
Node;AmplifyShaderEditor.RangedFloatNode;39;107.6089,932.4724;Float;False;Property;_HeadTailramp;Head-Tail ramp;10;0;Create;True;0;0;False;0;0;1.264919;0.1;3;0;1;FLOAT;0
Node;AmplifyShaderEditor.ColorNode;31;255.718,1234.08;Float;False;Property;_Tail;Tail;7;1;[HDR];Create;True;0;0;False;0;0.2006095,0,0,0;0,1.338968,4.703873,0;True;0;5;COLOR;0;FLOAT;1;FLOAT;2;FLOAT;3;FLOAT;4
Node;AmplifyShaderEditor.SimpleAddOpNode;10;-607.1567,148.1143;Float;False;2;2;0;FLOAT3;0,0,0;False;1;FLOAT3;0,0,0;False;1;FLOAT3;0
Node;AmplifyShaderEditor.LerpOp;35;746.5262,-662.8647;Float;True;3;0;COLOR;0,0,0,0;False;1;COLOR;0,0,0,0;False;2;FLOAT;0;False;1;COLOR;0
Node;AmplifyShaderEditor.ColorNode;30;270.192,1034.464;Float;False;Property;_Head;Head;6;1;[HDR];Create;True;0;0;False;0;0,0,0,0;0,5.378532,0.1099326,0;True;0;5;COLOR;0;FLOAT;1;FLOAT;2;FLOAT;3;FLOAT;4
Node;AmplifyShaderEditor.PowerNode;38;417.9113,854.8525;Float;False;2;0;FLOAT;0;False;1;FLOAT;1;False;1;FLOAT;0
Node;AmplifyShaderEditor.LerpOp;32;613.8838,977.2683;Float;False;3;0;COLOR;0,0,0,0;False;1;COLOR;0,0,0,0;False;2;FLOAT;0;False;1;COLOR;0
Node;AmplifyShaderEditor.RangedFloatNode;18;126.0073,290.1233;Float;False;Property;_Amplitude;Amplitude;3;0;Create;True;0;0;False;0;0;0.806;0;1;0;1;FLOAT;0
Node;AmplifyShaderEditor.ClipNode;26;1016.726,-462.6681;Float;False;3;0;COLOR;0,0,0,0;False;1;FLOAT;0;False;2;FLOAT;0;False;1;COLOR;0
Node;AmplifyShaderEditor.SamplerNode;8;-426.672,86.72348;Float;True;Property;_TextureSample1;Texture Sample 1;1;0;Create;True;0;0;False;0;None;b2edfc5ca1a77444693e7a61617f32cb;True;0;False;white;LockedToTexture3D;False;Object;-1;Auto;Texture3D;6;0;SAMPLER2D;;False;1;FLOAT3;0,0,0;False;2;FLOAT;0;False;3;FLOAT3;0,0,0;False;4;FLOAT3;0,0,0;False;5;FLOAT;1;False;5;COLOR;0;FLOAT;1;FLOAT;2;FLOAT;3;FLOAT;4
Node;AmplifyShaderEditor.SimpleMultiplyOpNode;16;472.6421,272.0987;Float;False;3;3;0;COLOR;0,0,0,0;False;1;FLOAT;0;False;2;FLOAT;0;False;1;COLOR;0
Node;AmplifyShaderEditor.SimpleMultiplyOpNode;33;1167.459,229.4445;Float;False;2;2;0;COLOR;0,0,0,0;False;1;COLOR;0,0,0,0;False;1;COLOR;0
Node;AmplifyShaderEditor.TemplateMultiPassMasterNode;3;1508.285,250.7779;Float;False;True;2;Float;ASEMaterialInspector;0;9;Custom/MeteorTrail;003dfa9c16768d048b74f75c088119d8;True;First;0;0;First;2;False;False;False;False;False;False;False;False;False;True;1;RenderType=TransparentCutout=RenderType;False;0;True;0;1;False;-1;0;False;-1;0;1;False;-1;0;False;-1;True;0;False;-1;0;False;-1;True;False;True;1;False;-1;True;True;True;True;True;0;False;-1;True;False;255;False;-1;255;False;-1;255;False;-1;7;False;-1;1;False;-1;1;False;-1;1;False;-1;7;False;-1;1;False;-1;1;False;-1;1;False;-1;True;1;False;-1;True;3;False;-1;True;True;0;False;-1;0;False;-1;True;0;True;7;0;;0;0;Standard;0;0;2;True;True;False;2;0;FLOAT4;0,0,0,0;False;1;FLOAT3;0,0,0;False;0
Node;AmplifyShaderEditor.TemplateMultiPassMasterNode;4;1508.285,376.5208;Float;False;False;2;Float;ASEMaterialInspector;0;9;ASESampleTemplates/DoublePassUnlit;003dfa9c16768d048b74f75c088119d8;True;Second;0;1;Second;2;False;False;False;False;False;False;False;False;False;True;1;RenderType=Opaque=RenderType;False;0;True;0;1;False;-1;0;False;-1;0;1;False;-1;0;False;-1;True;0;False;-1;0;False;-1;True;False;True;0;False;-1;True;True;True;True;True;0;False;-1;True;False;255;False;-1;255;False;-1;255;False;-1;7;False;-1;1;False;-1;1;False;-1;1;False;-1;7;False;-1;1;False;-1;1;False;-1;1;False;-1;True;1;False;-1;True;3;False;-1;True;True;0;False;-1;0;False;-1;True;0;True;7;0;;0;0;Standard;0;2;0;FLOAT4;0,0,0,0;False;1;FLOAT3;0,0,0;False;0
WireConnection;25;0;24;0
WireConnection;25;1;9;3
WireConnection;21;0;23;0
WireConnection;28;0;29;0
WireConnection;22;0;21;0
WireConnection;22;1;25;0
WireConnection;19;0;20;0
WireConnection;19;1;22;0
WireConnection;19;3;9;0
WireConnection;27;0;9;3
WireConnection;2;1;28;0
WireConnection;12;2;13;0
WireConnection;14;0;19;0
WireConnection;14;1;15;0
WireConnection;17;0;9;3
WireConnection;34;0;2;2
WireConnection;34;1;27;0
WireConnection;10;0;12;0
WireConnection;10;1;14;0
WireConnection;35;0;37;0
WireConnection;35;1;36;0
WireConnection;35;2;34;0
WireConnection;38;0;17;0
WireConnection;38;1;39;0
WireConnection;32;0;30;0
WireConnection;32;1;31;0
WireConnection;32;2;38;0
WireConnection;26;0;35;0
WireConnection;26;1;2;2
WireConnection;26;2;27;0
WireConnection;8;1;10;0
WireConnection;16;0;8;0
WireConnection;16;1;18;0
WireConnection;16;2;17;0
WireConnection;33;0;26;0
WireConnection;33;1;32;0
WireConnection;3;0;33;0
WireConnection;3;1;16;0
WireConnection;4;0;33;0
WireConnection;4;1;16;0
ASEEND*/
//CHKSM=7246C3DC63BAD4D803D9A32A72801511F7D4C4FA