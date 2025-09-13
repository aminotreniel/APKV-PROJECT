#ifndef __IMPOSTORUTILITY_HLSL__
#define __IMPOSTORUTILITY_HLSL__

#include "Packages/com.unity.render-pipelines.core/ShaderLibrary/SpaceTransforms.hlsl"


//Based on https://shaderbits.com/blog/octahedral-impostors

#define PLANE_DERIVATIVES_SHRINK 0.95f
#define PLANE_DERIVATIVES_SHRINK_INV (1.f/PLANE_DERIVATIVES_SHRINK)

float2 HemiOctEncode(float3 d)
{
    float2 t = d.xz * (1.0f / (abs(d.x) + abs(d.y) + abs(d.z)));
    return float2(t.x + t.y, t.y - t.x) * 0.5f + 0.5f;
}

float3 HemiOctDecode(float2 uv)
{
    float3 position = float3(uv.x - uv.y, 0.f, -1.0f + uv.x + uv.y);
    float2 absolute = abs(position.xz);
    position.y = 1.f - absolute.x - absolute.y;
    return position;
}

float2 OctEncode(float3 d)
{
    float sum = abs(d.x) + abs(d.y) + abs(d.z);        
    float3 p = d / sum;
    float t = saturate(-p.y);
    p.xz = sign(p.xz) * float2(abs(p.xz) + t);
    return p.xz * 0.5f + 0.5f;
}

float3 OctDecode(float2 uv)
{
    uv = uv * 2.f - 1.f;
    float3 p = float3(uv.x, 1.f - abs(uv.x) - abs(uv.y), uv.y);          
    float t = saturate(-p.y);
    p.xz = sign(p.xz) * float2(abs(p.xz) - t);
    return p;
}

float3 CalculateFrameSegmentAndCoordinates(float2 atlasUV, float2 gridResolution)
{
    uint2 resolutionMinusOne = gridResolution - 1;
    float2 gridSegment = atlasUV * resolutionMinusOne;
    
    float2 segment = floor(gridSegment);
    float2 fracts = gridSegment - segment;

    float segmentIndex = (segment.x + segment.y * resolutionMinusOne.x) + 0.5f;

    return float3(segmentIndex, fracts.x, fracts.y);
}

void CalculateFrameIndicesAndWeightsFromFrameSegmentAndCoordinates(float3 frameAndCoordinates, float2 resolution,
                                                                   out uint2 frame0Out, out uint2 frame1Out, out uint2 frame2Out, out float2 weightsOut)
{
    uint2 resolutionUint = resolution - 1.f;
    uint segmentIndex = floor(frameAndCoordinates.x);
    uint y = floor(segmentIndex / resolutionUint.x);
    uint x = floor(segmentIndex - y * resolutionUint.x);

    float2 fracts = frameAndCoordinates.yz;
    uint2 offset1 = float2(1.f, 1.f);
    uint2 offset2 = fracts.y > fracts.x ? uint2(0.f, 1.0f) : uint2(1.f, 0.f);

    frame0Out = uint2(x, y);
    frame1Out = frame0Out + offset1;
    frame2Out = frame0Out + offset2;

    //frame1Out = frame1Out % resolutionUint;
    //frame2Out = frame2Out % resolutionUint; 
    
    weightsOut = float2(fracts.x, fracts.y);
}

float3 CalculateBillboardVertex(float3 pos, float3 toCamera,  float scale)
{
    float3 up = float3(0, 1, 0);
    float3 billboardX = normalize(cross(toCamera, up));
    float3 billboardY = normalize(cross(billboardX, toCamera));

    return (billboardX * pos.x + billboardY * pos.y + toCamera * pos.z) * scale;
}

float3 CalculateWeights(float2 fracts)
{
    //diagonals
    float w0 = min(1.f - fracts.x, 1.f - fracts.y);
    float w1 = min(fracts.x, fracts.y);

    //right or top
    float w2 = abs(fracts.x - fracts.y);

    return float3(w0,w1,w2);
}

void UnpackFrameData(float4 frameData, out float2 uv, out float2 planeRay, out float2 frame)
{
    planeRay = frac(frameData.zw) * PLANE_DERIVATIVES_SHRINK_INV * 2.f - 1.f;
    uv = frameData.xy;
    frame = floor(frameData.zw);
}

float4 PackFrameData(float2 uv, float2 planeRay, float2 frame)
{
    planeRay.x = saturate((planeRay.x * 0.5f + 0.5f) * PLANE_DERIVATIVES_SHRINK);
    planeRay.y = saturate((planeRay.y * 0.5f + 0.5f) * PLANE_DERIVATIVES_SHRINK);

    return float4(uv.x, uv.y, frame.x + planeRay.x, frame.y + planeRay.y);
}

void SampleTextureOctahedron_float(float3 weights, float2 uv0, float2 uv1, float2 uv2, in SAMPLER(samp), in TEXTURE2D (tex), float2 dd, bool onlySampleOneFrame, out float4 output)
{
    if(onlySampleOneFrame)
    {
        float2 uv;
        if (weights.x > weights.y && weights.x > weights.z)
        {
            uv =  uv0;
        }
        else if (weights.y > weights.z)
        {
            uv =  uv1;
        }
        else
        {
            uv =  uv2;
        }

        output = SAMPLE_TEXTURE2D_GRAD(tex, samp, uv, dd.x, dd.y);
    }
    else
    {
        float4 v0 = SAMPLE_TEXTURE2D_GRAD(tex, samp, uv0, dd.x, dd.y);
        float4 v1 = SAMPLE_TEXTURE2D_GRAD(tex, samp, uv1, dd.x, dd.y);
        float4 v2 = SAMPLE_TEXTURE2D_GRAD(tex, samp, uv2, dd.x, dd.y);

        output = v0 * weights.x + v1 * weights.y + v2 * weights.z;
    }
}

float3 ApplyParallax(in TEXTURE2D (normalDepthTex), in SAMPLER (samp), float4 frameData, float2 gridResolutionInv, float depthOffsetScale, float2 uvDerivatives)
{
    float2 planeRay;
    float2 uv;
    float2 frame;
    UnpackFrameData(frameData, uv, planeRay, frame);
    
    float2 uvMax = (frame + 1.f) * gridResolutionInv;
    float2 uvMin = frame * gridResolutionInv;

    float2 uvFrame = (uv + frame) * gridResolutionInv;
    uvFrame = clamp(uvFrame, uvMin, uvMax);

    //single sample depth parallax
    float depth = SAMPLE_TEXTURE2D_GRAD(normalDepthTex, samp, uvFrame, uvDerivatives.x, uvDerivatives.y).w;

    float2 uvOffset = planeRay * (0.5f - depth) * depthOffsetScale * gridResolutionInv;
    uvFrame += uvOffset;

    return float3(clamp(uvFrame, uvMin, uvMax), depth);
}


void ApplyWind_float(
    in float4 frameData0,
    in float4 frameData1,
    in float4 frameData2,
    in float3 windDirection,
    in float2 gridResolution,
    in bool hemiOcta,
    out float4 frameData0Out,
    out float4 frameData1Out,
    out float4 frameData2Out)
{
    float2 resInv = 1.f / (gridResolution - 1);
    float4 frameData[] = {frameData0, frameData1, frameData2};
    for(int i = 0; i < 3; ++i)
    {
        float2 uv;
        float2 planeRay;
        float2 frame;
        UnpackFrameData(frameData[i], uv, planeRay, frame);

        float3 frameDir = normalize(hemiOcta ? HemiOctDecode(frame * resInv) : OctDecode(frame * resInv));
        float3 up = float3(0, 1, 0);
        float3 frameAxisX = normalize(cross(frameDir, up));
        float3 frameAxisY = normalize(cross(frameAxisX, frameDir));

        float px = dot(frameAxisX, windDirection);
        float py = dot(frameAxisY, windDirection);

        uv += float2(px, py);

        frameData[i] = PackFrameData(uv, planeRay, frame);
    }

    frameData0Out = frameData[0];
    frameData1Out = frameData[1];
    frameData2Out = frameData[2];
}

void OrientBillboard_float(
    in float3 cornerPos,
    in float3 toCamera,
    in float planeScale,
    out float3 dstPosition)
{
    dstPosition = CalculateBillboardVertex(cornerPos, toCamera, planeScale);
}

void SelectSingleFrame_float(
    in float4 frameData0,
    in float4 frameData1,
    in float4 frameData2,
    in float2 fracts,
    out float4 frameDataOut)
{
    float3 weights = CalculateWeights(fracts);
    
    if (weights.x > weights.y && weights.x > weights.z)
    {
        frameDataOut =  frameData0;
    }
    else if (weights.y > weights.z)
    {
        frameDataOut =  frameData1;
    }
    else
    {
        frameDataOut =  frameData2;
    }
}


void CalculateImpostorFrameData_float(
    in float2 resolution,
    in float3 cameraPosition,
    in float3 cornerPos,
    in float projectionScale,
    in bool hemiOcta,
    in bool singleFrame,
    out float4 frameData0Out,
    out float4 frameData1Out,
    out float4 frameData2Out,
    out float2 frameData3Out)
{
    float3 origoToCamera = normalize(cameraPosition);

    float2 atlasUV = hemiOcta ? HemiOctEncode(origoToCamera) : OctEncode(origoToCamera);

    float3 triangleAndCoords = CalculateFrameSegmentAndCoordinates(atlasUV, resolution);

    //calculate uvs for all the frames projected to the current plane
    float2 frames[3];
    float4 frameData[3];
    float2 weights;
    CalculateFrameIndicesAndWeightsFromFrameSegmentAndCoordinates(triangleAndCoords, resolution, frames[0], frames[1], frames[2], weights);

    float3 billboardCorner = cornerPos;
    float3 cameraToBillboardCornerDir = normalize(billboardCorner - cameraPosition);

    float2 resInv = 1.f / (resolution - 1);
    for (uint i = 0u; i < 3u; ++i)
    {
        float2 frame = frames[i];
        float3 frameDir = normalize(hemiOcta ? HemiOctDecode(frame * resInv) : OctDecode(frame * resInv));
        float3 up = float3(0, 1, 0);
        float3 frameAxisX = normalize(cross(frameDir, up));
        float3 frameAxisY = normalize(cross(frameAxisX, frameDir));
        
        float u = dot(frameAxisX, billboardCorner) * projectionScale * 0.5f + 0.5f;
        float v = dot(frameAxisY, billboardCorner) * projectionScale * 0.5f + 0.5f;

        float tx = dot(frameAxisX, cameraToBillboardCornerDir);
        float ty = dot(frameAxisY, cameraToBillboardCornerDir);

        frameData[i] = PackFrameData(float2(u, v), float2(tx, ty), frame);
    }

    if(singleFrame)
    {
        float4 mostImportantFrame;
        SelectSingleFrame_float(frameData[0], frameData[1], frameData[2], weights, mostImportantFrame);
        frameData[0] = mostImportantFrame;
        frameData[1] = 0;
        frameData[1] = 0;
        weights = 1;
    }
    
    frameData0Out = frameData[0];
    frameData1Out = frameData[1];
    frameData2Out = frameData[2];
    frameData3Out = weights;
}


void SampleTextureOctahedron_float(
    in float4 frameData0,
    in float4 frameData1,
    in float4 frameData2,
    in float2 frameData3,
    in bool useOnlySingleFrame,
    in float2 gridResolution,
    in TEXTURE2D (depthTex),
    in SAMPLER (samp),
    in float depthOffsetScale,
    in float2 uvDerivatives,
    out float2 uv0Out,
    out float2 uv1Out,
    out float2 uv2Out,
    out float3 sampleWeightsOut,
    out float noParallaxDepth)
{
    float2 gridResInv = 1.f / gridResolution;

    
    
    float2 uv0;
    float2 uv1;
    float2 uv2;
    if(useOnlySingleFrame)
    {
        float4 singleFrameData;
        SelectSingleFrame_float(frameData0, frameData1, frameData2, frameData3, singleFrameData);
        float3 uvDepth = ApplyParallax(depthTex, samp, singleFrameData, gridResInv, depthOffsetScale, uvDerivatives);
        uv0 = uvDepth.xy;
        uv1 = 0;
        uv2 = 0;
        sampleWeightsOut = float3(1, 0, 0);
        noParallaxDepth = uvDepth.z;
    }
    else
    {
        float3 uvDepth0 = ApplyParallax(depthTex, samp, frameData0, gridResInv, depthOffsetScale, uvDerivatives);
        float3 uvDepth1 = ApplyParallax(depthTex, samp, frameData1, gridResInv, depthOffsetScale, uvDerivatives);
        float3 uvDepth2 = ApplyParallax(depthTex, samp, frameData2, gridResInv, depthOffsetScale, uvDerivatives);
        uv0 = uvDepth0.xy;
        uv1 = uvDepth1.xy;
        uv2 = uvDepth2.xy;
        sampleWeightsOut = CalculateWeights(frameData3);
        noParallaxDepth = uvDepth0.z * sampleWeightsOut.x + uvDepth1.z * sampleWeightsOut.y + uvDepth2.z * sampleWeightsOut.z;
    }
    
    uv0Out = uv0;
    uv1Out = uv1;
    uv2Out = uv2;
}

void IsShadowPass_float(
    out bool val)
{
    #if defined(SHADERPASS) && (SHADERPASS != SHADERPASS_SHADOWS)
    val = false;
    #else
    val = true;
    #endif
}

void GetCameraPosition_float(out float3 pos)
{
    #if UNITY_REVERSED_Z
    float4 p = float4(0.0f, 0.0f, 1.0f, 1.0);
    #else
    float4 p = float4(0.0f, 0.0f, 0.0f, 1.0);
    #endif
    
    p = mul(UNITY_MATRIX_I_VP, p);
    pos = p.xyz/p.w;
}

void ApplyDepthOffsetToImpostorPlane_float(float3 p, float impostorDepthSpan, out float3 posOut)
{
    float4x4 projMatrix = UNITY_MATRIX_P;
    float4x4 projInvMatrix = UNITY_MATRIX_I_P;

    float halfSpan = impostorDepthSpan * 0.5f;
    #if UNITY_REVERSED_Z
    float depthOffset = halfSpan;
    #else
    float depthOffset = -halfSpan;
    #endif

    float4 pos = float4(TransformWorldToView(TransformObjectToWorld(p)), 1.f);
    
    depthOffset = depthOffset * abs(normalize(pos.xyz).z);
    
    float4 pos2 = pos;
    pos2.z += depthOffset;
    pos2 = mul(projMatrix, pos2);
    float depthPostProj = pos2.z /= pos2.w; 
    
    pos = mul(projMatrix, pos);
    pos /= pos.w;
    pos.z = depthPostProj;
    
    pos = mul(projInvMatrix, pos);
    pos /= pos.w;

    posOut = TransformWorldToObject(TransformViewToWorld(pos.xyz));
}


#endif
