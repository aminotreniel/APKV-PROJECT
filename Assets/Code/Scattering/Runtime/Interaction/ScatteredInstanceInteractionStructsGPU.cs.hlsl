//
// This file was automatically generated. Please don't edit by hand. Execute Editor command [ Edit > Rendering > Generate Shader Includes ] instead
//

#ifndef SCATTEREDINSTANCEINTERACTIONSTRUCTSGPU_CS_HLSL
#define SCATTEREDINSTANCEINTERACTIONSTRUCTSGPU_CS_HLSL
//
// TimeGhost.InstancePropertiesFlags:  static fields
//
#define INSTANCEPROPERTIESFLAGS_INSTANCE_PERMANENT_DAMAGE (1)

//
// TimeGhost.ScatteredInstanceInteractionDataSettings:  static fields
//
#define SCATTERED_INSTANCE_INTERACTION_PAGE_SIZE (128)

// Generated from TimeGhost.PerTileHeaderEntry
// PackingRules = Exact
struct PerTileHeaderEntry
{
    uint _PageCount;
    uint _PageOffset;
    uint _EntryCount;
    uint _GlobalTileIndex;
};

// Generated from TimeGhost.ScatteredInstanceDataUploadBatch
// PackingRules = Exact
struct ScatteredInstanceDataUploadBatch
{
    int tileIndex;
    int perTilePageOffset;
    int entryCount;
    int padding;
};

// Generated from TimeGhost.ScatteredInstanceInteractionConstants
// PackingRules = Exact
CBUFFER_START(ScatteredInstanceInteractionConstants)
    int4 _ActiveGlobalTileDimensions;
    float4 _ColliderMarginUnused;
CBUFFER_END

// Generated from TimeGhost.ScatteredInstancePropertiesPacked
// PackingRules = Exact
struct ScatteredInstancePropertiesPacked
{
    uint4 _SpringDataPlasticityPacked;
    uint4 _PositionFlags;
};

// Generated from TimeGhost.ScatteredInstanceStatePacked
// PackingRules = Exact
struct ScatteredInstanceStatePacked
{
    uint4 _OffsetStiffnessVelocityDamping;
    uint4 _EquilibriumUnused;
};


#endif
