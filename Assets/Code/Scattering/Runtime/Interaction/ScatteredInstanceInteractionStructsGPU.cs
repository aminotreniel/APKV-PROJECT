using System;
using System.Collections;
using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;

namespace TimeGhost
{
    [GenerateHLSL]
    public struct ScatteredInstanceInteractionDataSettings
    {
        public const uint SCATTERED_INSTANCE_INTERACTION_PAGE_SIZE = 128;
    }
    
    [Flags, GenerateHLSL]
    public enum InstancePropertiesFlags : uint
    {
        InstancePermanentDamage			= 1 << 0,
    }

    [GenerateHLSL(needAccessors = false, generateCBuffer = true)]
    public struct ScatteredInstanceInteractionConstants
    {
        public int4 _ActiveGlobalTileDimensions; //xy == active tile grid dimensions, zw == global tile grid dimensions
        public float4 _ColliderMarginUnused; //x == collider margin, yzw unused
    }
    
    [GenerateHLSL(needAccessors = false)]
    public struct ScatteredInstanceDataUploadBatch
    {
        public int tileIndex;
        public int perTilePageOffset;
        public int entryCount;
        public int padding;
    }

    [GenerateHLSL(needAccessors = false)]
    public struct PerTileHeaderEntry
    {//x = page count, y = page offset, z = total number of entries per tile, w = absolute tile index (0xFFFFFF if not present)
        public uint _PageCount;
        public uint _PageOffset; 
        public uint _EntryCount;
        public uint _GlobalTileIndex;
    }

    [GenerateHLSL(needAccessors = false)]
    public struct ScatteredInstancePropertiesPacked
    {
        public uint4 _SpringDataPlasticityPacked;
        public uint4 _PositionFlags;
    }

    [GenerateHLSL(needAccessors = false)]
    public struct ScatteredInstanceStatePacked
    {
        public uint4 _OffsetStiffnessVelocityDamping;
        public uint4 _EquilibriumUnused;
    }
    
}