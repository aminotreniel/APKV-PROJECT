#ifndef __SCATTEREDINSTANCEDATALOOKUP_HLSL__
#define __SCATTEREDINSTANCEDATALOOKUP_HLSL__
#include "ScatteredInstanceInteractionCommon.hlsl"

void GetScatteredInstanceData_float(
	in float2 in_tileIndices,
	in bool in_IncludeDebugData,
	out float3 out_interactiveOffset,
	out float3 out_interactiveOffsetPrevious,
	out float4 out_axisAngle,
	out float4 out_axisAnglePrevious,
	out float3 out_debugColor,
	out float out_debugBroke
	)
{
	float3 neutralDir = float3(0, 1, 0);
	bool debugDataValid = false;
	
	#if SCATTERED_INSTANCE_INTERACTION_ENABLED
	int2 tileIndices = in_tileIndices;
	int globalFlatTileIndex = tileIndices.x;
	int inTileInstanceOffset = tileIndices.y;
	
	ScatteredInstanceState state;
	ScatteredInstanceProperties props; 
	if(TryFetchPerInstanceInteractionState(globalFlatTileIndex, inTileInstanceOffset, in_IncludeDebugData, state, props))
	{
		if(in_IncludeDebugData)
		{
			out_debugBroke = (props.flags & INSTANCEPROPERTIESFLAGS_INSTANCE_PERMANENT_DAMAGE) != 0;
			out_debugColor = frac(abs(state.offset) * 0.1f);
			debugDataValid = true;
		} 
		
		
		out_interactiveOffset = state.offset;
		out_interactiveOffsetPrevious = state.prevOffset;

		{
			float3 normalizedDir = normalize(state.offset);
			
			float3 axis = cross(normalizedDir, neutralDir);
			float angleCosine = dot(normalizedDir, neutralDir);
			float angle = acos(angleCosine);
			if(dot(neutralDir, cross(normalizedDir, axis)) < 0)
			{
				angle = -angle;
			}
			out_axisAngle = float4(axis, angle);
		}
			
		{
			float3 normalizedDir = normalize(state.prevOffset);
			
			float3 axis = cross(normalizedDir, neutralDir);
			float angleCosine = dot(normalizedDir, neutralDir);
			float angle = acos(angleCosine);
			if(dot(neutralDir, cross(normalizedDir, axis)) < 0)
			{
				angle = -angle;
			}
			out_axisAnglePrevious = float4(axis, angle);
		}
		
	} else
	#endif
	{
		out_interactiveOffset = 0;
		out_interactiveOffsetPrevious = 0;
		out_axisAngle = float4(neutralDir, 0);
		out_axisAnglePrevious = float4(neutralDir, 0);
	}

	if(!debugDataValid)
	{
		out_debugColor = 0;
		out_debugBroke = 0;
	}
	
}

#endif//__SCATTEREDINSTANCEDATALOOKUP_HLSL__
