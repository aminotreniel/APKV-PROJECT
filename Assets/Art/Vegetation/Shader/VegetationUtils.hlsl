#ifndef __VEGETATIONUTILS_HLSL__
#define __VEGETATIONUTILS_HLSL__

float3 QMul(float4 q, float3 v)
{
    float3 t = 2.0 * cross(q.xyz, v);
    return v + q.w * t + cross(q.xyz, t);
}

float4 MakeQuaternionFromTo(float3 u, float3 v)
{
    float4 q;
    float s = 1.0 + dot(u, v);
    if (s < 1e-6)// if 'u' and 'v' are directly opposing
    {
        q.xyz = abs(u.x) > abs(u.z) ? float3(-u.y, u.x, 0.0) : float3(0.0, -u.z, u.y);
        q.w = 0.0;
    }
    else
    {
        q.xyz = cross(u, v);
        q.w = s;
    }
    return normalize(q);
}


//Note that this does not take into account potential twist in the transformation
void ReorientNormalAndTangentApprox_float(
    in float3 neutralPos,
    in float3 newPos,
    in float3 normal,
    in float3 tangent,
    out float3 normalOut,
    out float3 tangentOut
    )
{
    float4 quat = MakeQuaternionFromTo(normalize(neutralPos), normalize(newPos));
    normalOut = QMul(quat, normal);
    tangentOut = QMul(quat, tangent);
}

#endif