#ifndef __ScatteredInstanceInteractionData_HLSL__
#define __ScatteredInstanceInteractionData_HLSL__

#include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Common.hlsl"
#include "ScatteredInstanceInteractionStructsGPU.cs.hlsl"
struct ScatteredInstanceState
{
    float3 offset;
    float stiffness;
    float3 velocity;
    float damping;
    float3 equilibrium;
    float3 prevOffset;
};

struct ScatteredInstanceProperties
{
    float3 position;
    float3 tipOffset;
    float2 plasticityParams;//x == cosine of "breaking angle" after which permanent deformation occurs, y == cosine of angle to which a broken 
    float tipRadius;
    float damping;
    float stiffness;
    uint flags;
};

#define INVALID_ABSOLUTE_TILE_INDEX 0xFFFFFFFF

StructuredBuffer<PerTileHeaderEntry> _PerTileHeaderBuffer;
ByteAddressBuffer _ReservedPagesBuffer;
ByteAddressBuffer _PerInstanceStateBuffer;
ByteAddressBuffer _PerInstancePropertiesBuffer;

float3 ColorCycle(uint index, uint count)
{
    float t = frac(index / (float)count);

    // source: https://www.shadertoy.com/view/4ttfRn
    float3 c = 3.0 * float3(abs(t - 0.5), t.xx) - float3(1.5, 1.0, 2.0);
    return 1.0 - c * c;
}

int GlobalFlatTileIndexToActiveFlatTileIndex(uint flatPerInstanceTileIndex)
{
    uint2 activetileDimensions = _ActiveGlobalTileDimensions.xy;
    uint2 globalTileDimensions = _ActiveGlobalTileDimensions.zw;

    uint2 globalIndex = uint2(flatPerInstanceTileIndex % globalTileDimensions.x, flatPerInstanceTileIndex / globalTileDimensions.x);
    uint2 activeIndex = uint2(globalIndex.x % activetileDimensions.x, globalIndex.y % activetileDimensions.y);

    return activeIndex.x + activeIndex.y * activetileDimensions.x;
}

void PackInstanceState(in ScatteredInstanceState stateUnpacked, out ScatteredInstanceStatePacked stateOut)
{
    stateOut._OffsetStiffnessVelocityDamping = uint4(
        (f32tof16(stateUnpacked.offset.x) << 16) | (f32tof16(stateUnpacked.offset.y) & 0xFFFF),
        (f32tof16(stateUnpacked.offset.z) << 16) | (f32tof16(stateUnpacked.stiffness) & 0xFFFF),
        (f32tof16(stateUnpacked.velocity.x) << 16) | (f32tof16(stateUnpacked.velocity.y) & 0xFFFF),
        (f32tof16(stateUnpacked.velocity.z) << 16) | (f32tof16(stateUnpacked.damping) & 0xFFFF)
        );
    stateOut._EquilibriumUnused = uint4(
        (f32tof16(stateUnpacked.equilibrium.x) << 16) | (f32tof16(stateUnpacked.equilibrium.y) & 0xFFFF),
        (f32tof16(stateUnpacked.equilibrium.z) << 16) | (f32tof16(stateUnpacked.prevOffset.x) & 0xFFFF),
        (f32tof16(stateUnpacked.prevOffset.y) << 16) | (f32tof16(stateUnpacked.prevOffset.z) & 0xFFFF),
        0);

    
}

void UnpackInstanceState(ScatteredInstanceStatePacked state, out ScatteredInstanceState stateUnpacked)
{
    {
        uint4 packedData = state._OffsetStiffnessVelocityDamping;
        stateUnpacked.offset.x = f16tof32(packedData.x >> 16);
        stateUnpacked.offset.y = f16tof32(packedData.x & 0xFFFF);
        stateUnpacked.offset.z = f16tof32(packedData.y >> 16);
        stateUnpacked.stiffness = f16tof32(packedData.y & 0xFFFF);
        stateUnpacked.velocity.x = f16tof32(packedData.z >> 16);
        stateUnpacked.velocity.y = f16tof32(packedData.z & 0xFFFF);
        stateUnpacked.velocity.z = f16tof32(packedData.w >> 16);
        stateUnpacked.damping = f16tof32(packedData.w & 0xFFFF);
    }
    {
        uint4 packedData2 = state._EquilibriumUnused;
        stateUnpacked.equilibrium.x = f16tof32(packedData2.x >> 16);
        stateUnpacked.equilibrium.y = f16tof32(packedData2.x & 0xFFFF);
        stateUnpacked.equilibrium.z = f16tof32(packedData2.y >> 16);

        stateUnpacked.prevOffset.x = f16tof32(packedData2.y & 0xFFFF);
        stateUnpacked.prevOffset.y = f16tof32(packedData2.z >> 16);
        stateUnpacked.prevOffset.z = f16tof32(packedData2.z & 0xFFFF);
    }
}

void PackInstanceProperties(in ScatteredInstanceProperties propsUnpacked, out ScatteredInstancePropertiesPacked propsOut)
{
    propsOut._PositionFlags = uint4(asuint(propsUnpacked.position), propsUnpacked.flags);
    propsOut._SpringDataPlasticityPacked = uint4(
        (f32tof16(propsUnpacked.tipOffset.x) << 16) | (f32tof16(propsUnpacked.tipOffset.y) & 0xFFFF),
        (f32tof16(propsUnpacked.tipOffset.z) << 16) | (f32tof16(propsUnpacked.tipRadius) & 0xFFFF),
        (f32tof16(propsUnpacked.damping) << 16) | (f32tof16(propsUnpacked.stiffness) & 0xFFFF),
        (f32tof16(propsUnpacked.plasticityParams.x) << 16) | (f32tof16(propsUnpacked.plasticityParams.y) & 0xFFFF)
        );
}

void UnpackInstanceProperties(ScatteredInstancePropertiesPacked props, out ScatteredInstanceProperties propsUnpacked)
{
    propsUnpacked.position = asfloat(props._PositionFlags.xyz);
    propsUnpacked.flags = props._PositionFlags.w;

    uint4 packedData = props._SpringDataPlasticityPacked;
    propsUnpacked.tipOffset.x = f16tof32(packedData.x >> 16);
    propsUnpacked.tipOffset.y = f16tof32(packedData.x & 0xFFFF);
    propsUnpacked.tipOffset.z = f16tof32(packedData.y >> 16);
    propsUnpacked.tipRadius = f16tof32(packedData.y & 0xFFFF);
    propsUnpacked.damping = f16tof32(packedData.z >> 16);
    propsUnpacked.stiffness = f16tof32(packedData.z & 0xFFFF);
    propsUnpacked.plasticityParams.x = f16tof32(packedData.w >> 16);
    propsUnpacked.plasticityParams.y = f16tof32(packedData.w & 0xFFFF);

}

void LoadPerInstanceState(int index, out ScatteredInstanceState state)
{
    uint offset1 = 8 * (uint)index;
    uint offset2 = offset1 + 4;
    ScatteredInstanceStatePacked packed;
    packed._OffsetStiffnessVelocityDamping = _PerInstanceStateBuffer.Load4(offset1 << 2);
    packed._EquilibriumUnused = _PerInstanceStateBuffer.Load4(offset2 << 2);
    UnpackInstanceState(packed, state);
}

void LoadPerInstanceProperties(int index, out ScatteredInstanceProperties props)
{
    uint offset1 = 8 * (uint)index;
    uint offset2 = offset1 + 4;
    ScatteredInstancePropertiesPacked packed;
    packed._PositionFlags = _PerInstancePropertiesBuffer.Load4(offset1 << 2);
    packed._SpringDataPlasticityPacked = _PerInstancePropertiesBuffer.Load4(offset2 << 2);
    UnpackInstanceProperties(packed, props);
}

bool TryFetchPerInstanceInteractionState(int flatPerInstanceTileIndex, uint perTileInstanceOffset, bool includeProperties, out ScatteredInstanceState stateOut, out ScatteredInstanceProperties propertiesOut)
{
    int tileLookupIndex = GlobalFlatTileIndexToActiveFlatTileIndex(flatPerInstanceTileIndex);
    bool hasValidState = false;
    if(tileLookupIndex >= 0)
    {
        PerTileHeaderEntry headerEntry = _PerTileHeaderBuffer[tileLookupIndex];
        if(headerEntry._GlobalTileIndex == (uint)flatPerInstanceTileIndex && headerEntry._PageCount > 0)
        {
            uint pageOffsetInTile = perTileInstanceOffset / SCATTERED_INSTANCE_INTERACTION_PAGE_SIZE;
            uint offsetInPage = perTileInstanceOffset % SCATTERED_INSTANCE_INTERACTION_PAGE_SIZE;

            uint offsetToReservedPageIndices = headerEntry._PageOffset + pageOffsetInTile;
            int pageIndex = _ReservedPagesBuffer.Load(offsetToReservedPageIndices << 2);
            int instanceEntryIndex = pageIndex * SCATTERED_INSTANCE_INTERACTION_PAGE_SIZE + offsetInPage;
            
            ScatteredInstanceState perInstanceState;
            LoadPerInstanceState(instanceEntryIndex, perInstanceState);
            
            stateOut = perInstanceState;

            if(includeProperties)
            {
                ScatteredInstanceProperties perInstanceProps;
                LoadPerInstanceProperties(instanceEntryIndex, perInstanceProps);
                propertiesOut = perInstanceProps;
            }
            else
            {
                propertiesOut = (ScatteredInstanceProperties)0;
            }
            
            hasValidState = true;
        }
		
    }

    if(!hasValidState)
    {
        stateOut = (ScatteredInstanceState)0;
        propertiesOut = (ScatteredInstanceProperties)0;
    }

    return hasValidState;
}
#endif