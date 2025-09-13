#ifndef __TILEIMPOSTOR_HLSL__
#define __TILEIMPOSTOR_HLSL__

#include "Packages/com.unity.render-pipelines.core/ShaderLibrary/SpaceTransforms.hlsl"
#include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Random.hlsl"

float CalculateAboveViewFraction(float3 viewDirOS)
{
    float view = abs(viewDirOS.y);
    return view < 0.8 ? 0 : smoothstep(0.8, 1, view);
}


float4 SampleTileMeshDetail(in TEXTURE2D (tex), in SAMPLER (samp), float2 frameIndices, float4 uv0, float uvScale, float4 atlasRes, float4 frameRes, float frameOffset)
{

    uv0.xyz += frameOffset;
    float3 scaledUV = uv0.xyz * uvScale;
    
    float2 sampleUVSide = float2(scaledUV.z, uv0.w);

    //add height variance
    {
        float2 sizeVariance = float2(1.f, 1.25f);
        float t = GenerateHashedRandomFloat(floor(sampleUVSide.x));
        sampleUVSide = frac(sampleUVSide);
        sampleUVSide.y = saturate(sampleUVSide.y * lerp(sizeVariance.x, sizeVariance.y, t));
        
    }
    
    float marginMultiplier = sampleUVSide.y < 1.f ? 1.f : 0.f; //atlas should have margin itself but for now done here
    float2 frameCoordinates = sampleUVSide * (frameRes.xy - 1);
    sampleUVSide = (frameIndices * frameRes.xy + frameCoordinates)  * atlasRes.zw;
    float4 sampleSide = SAMPLE_TEXTURE2D(tex, samp, sampleUVSide);
    sampleSide.a *= marginMultiplier;
    return sampleSide;
}

float3 OrientDetailNormal(float3 detailNormal, float3 geometryNormal, float3 geometryTangent)
{
    detailNormal *= 2.0f;
    detailNormal -= 1.0f;
    detailNormal = normalize(detailNormal);
    float3 bitangent = cross(geometryNormal, geometryTangent);
    float3x3 transf = float3x3(geometryTangent, geometryNormal, bitangent);
    return normalize(mul(transf, detailNormal));
}

float3 CalculateImpostorCardVertex(
    in float4 uv0,
    in uint verteIndex,
    in float cardWidthInMeters)
{
    uint vIndex = verteIndex % 4;
    
    float offsX = -0.5f + (vIndex % 2);
    float offsY = vIndex > 1 ? uv0.z : uv0.w;

    float x = uv0.x + offsX * cardWidthInMeters;
    float y = offsY;
    float z = uv0.y;

    return float3(x, y, z);
}

void ApplyHeightOffset_float(
    in float3 position,
    in float3 viewDirOS,
    in float2 heightMinMax,
    out float3 outPosition)
{
    float f = CalculateAboveViewFraction(viewDirOS);
    position.y = lerp(position.y, heightMinMax.y, f);
    outPosition = position;
}

void OrientTileCard(float3 pos, float3 pivot, float3 cameraPosition, float2 cardSize, float3 windOffset, float windCoeff, out float3 posOut, out float3 posOutNoWind, out float3 normalOut, out float3 tangentOut)
{
    float3 viewVector = cameraPosition - pivot;
    float dist = length(viewVector);
    float3 viewDir = viewVector / max(dist, 0.00001f);

    if(abs(viewDir.y) > 0.9f)
    {
        viewDir.y = 0.9f;
        viewDir = normalize(viewDir);
    }
    
    {
        
        float3 up = abs(viewDir.y) > 0.99f ? float3(0, 0, 1) : float3(0, 1, 0);
        float3 right = normalize(cross(viewDir, up));
        up = cross(right, viewDir);
        //with wind
        {
            float3 wind = windOffset * windCoeff;
            wind.y -= max(abs(wind.x),abs(wind.z));
            float3 pivotRelativePos = (pos + wind) - pivot;
            posOut = pivot + pivotRelativePos.x * right + pivotRelativePos.y * up + pivotRelativePos.z * viewDir;
        }

        //without wind
        {
            float3 pivotRelativePos = pos - pivot;
            posOutNoWind = pivot + pivotRelativePos.x * right + pivotRelativePos.y * up + pivotRelativePos.z * viewDir;
        }
        
        normalOut = up;
        tangentOut = right;
    }
}
void CalculateTileCardGeometryData_float(
    in float vertexIndex,
    in float4 uvIn,
    in float4 tileSize,
    in float3 cameraPositionOS,
    in float2 cardWidthAndInv,
    in float3 windOffset,
    in float randomSkewStr,
    out float3 positionOut,
    out float3 normalOut,
    out float3 tangentOut,
    out float4 outUV)
{
    float3 position = CalculateImpostorCardVertex(uvIn, vertexIndex, cardWidthAndInv.x);
    float uRange = abs(tileSize.x);
    float vRange = abs(tileSize.z);
    float2 pivotxz = uvIn.xy;
    float2 heightMinMax = uvIn.zw;
    float3 pivot = float3(pivotxz.x, heightMinMax.x * 0.5f + heightMinMax.y * 0.5f, pivotxz.y);
    float v = (position.y - heightMinMax.x) / (heightMinMax.y - heightMinMax.x);

    float orientationOffset = 0;
    {
        float3 relPos = position - pivot;
        float skew = randomSkewStr;
        float cardIndex = vertexIndex / 4;
        float t = GenerateHashedRandomFloat(cardIndex);
        skew = -skew + 2 * t * skew;
        orientationOffset = relPos.y * skew;
        position.x += orientationOffset;
    }
    
    float3 positionWithoutWind;
    OrientTileCard(position, pivot, cameraPositionOS, cardWidthAndInv, windOffset, v, positionOut, positionWithoutWind,  normalOut, tangentOut);

    orientationOffset = orientationOffset + (positionOut.x - position.x);

    float tileu = (positionWithoutWind.x + uRange * 0.5f) / uRange;
    float tilev = (positionWithoutWind.z + vRange * 0.5f) / vRange;
    
    float offsetX = position.x - orientationOffset - pivot.x;
    float u = offsetX + cardWidthAndInv.x * 0.5f;
    //u *= cardWidthAndInv.y;
    
    outUV = float4(tileu, tilev, u, v);
}

void SampleTileMeshDetailTexture_float(
    in TEXTURE2D (albedoAlpha),
    in TEXTURE2D (normalDepth),
    in SAMPLER (samp),
    in float4 uv0,
    in float4 frameIndices,
    in float uvScale,
    in float3 normal,
    in float3 tangent,
    in float3 viewDir,
    in float4 atlasResolution,
    in float4 atlasProxyResolution,
    in float aboveCoeff,
    out float4 albedoAlphaOut,
    out float4 normalDepthOut)
{
    uint frameIndex = floor(uv0.z);
    float t = GenerateHashedRandomFloat(frameIndex);
    float frameIndexX;
    if(t < 0.25f)
    {
        frameIndexX = frameIndices.x;
    }
    else if(t < 0.5f)
    {
        frameIndexX = frameIndices.y;
    }
    else if(t < 0.75f)
    {
        frameIndexX = frameIndices.z;
    }
    else
    {
        frameIndexX = frameIndices.w;
    }

    float2 frameIndicesToUse = float2(frameIndexX, 0);

    float frameOffset = 0.5f;
    bool useFirstProxy = true;
    {
        float4 sample0 = SampleTileMeshDetail(albedoAlpha, samp, frameIndicesToUse, uv0, uvScale, atlasResolution, atlasProxyResolution, 0);
        float4 sample1 = SampleTileMeshDetail(albedoAlpha, samp, frameIndicesToUse, uv0, uvScale, atlasResolution, atlasProxyResolution, frameOffset);

        float4 s = sample0;
        if(sample1.a > sample0.a)
        {
            useFirstProxy = false;
            s = sample1;
        }
        
        albedoAlphaOut = s;
    }
    
    {
        float4 normalSample;
        if(useFirstProxy)
        {
            normalSample = SampleTileMeshDetail(normalDepth, samp, frameIndicesToUse, uv0, uvScale, atlasResolution, atlasProxyResolution, 0);
        }
        else
        {
            normalSample = SampleTileMeshDetail(normalDepth, samp, frameIndicesToUse, uv0, uvScale, atlasResolution, atlasProxyResolution, frameOffset);
        }
        
        normalDepthOut.xyz = OrientDetailNormal(normalSample.xyz, normal, tangent);
        normalDepthOut.a = normalSample.a;
    }
}

#endif