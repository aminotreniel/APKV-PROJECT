#ifndef __SCATTERINGSGFUNCTIONS_HLSL__
#define __SCATTERINGSGFUNCTIONS_HLSL__



void UnpackPerInstanceColor_float(
	in float packedColor,
	out float4 unpackedColor
	)
{
	const float repr = 1.f/255.f;
	uint p = asuint(packedColor);
	unpackedColor.x = p & 0xFF;
	unpackedColor.y = (p >> 8) & 0xFF;
	unpackedColor.z = (p >> 16) & 0xFF;
	unpackedColor.w = (p >> 24) & 0xFF;
	unpackedColor *= repr;
}

#endif