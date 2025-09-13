#ifndef __SCATTEREDINSTANCEINTERSECTIONUTILITY_HLSL__
#define __SCATTEREDINSTANCEINTERSECTIONUTILITY_HLSL__

#include "ScatteredInstanceInteractionStructsGPU.cs.hlsl"



//from com.unity.demoteam.hair
float4 MakeQuaternionFromAxisAngle(float3 axis, float angle)
{
	float sina = sin(0.5 * angle);
	float cosa = cos(0.5 * angle);
	return float4(axis * sina, cosa);
}

float3 QMul(float4 q, float3 v)
{
    float3 t = 2.0 * cross(q.xyz, v);
    return v + q.w * t + cross(q.xyz, t);
}

float3 CalculateClosestPointToCapsule(float3 c, float3 p0, float3 p1)
{
    const float EPSILON = 1e-6;
    //calculate closest point to line segment defining the capsule
    float3 closestPoint;
    {
        float3 v = p1 - p0;
        float vLen = length(v);
        if(vLen > EPSILON)
        {
            v /= vLen;

            float3 toCenter = c - p0;
            float t = dot(toCenter, v);

            if (t < 0)
            {
                closestPoint = p0;
            } 
            else if (t > vLen)
            {
                closestPoint = p1;
            }
            else
            {
                closestPoint = p0 + t * v;
            }
        } else
        {
            closestPoint = p0;
        }
        
    }
    return closestPoint;
}

bool IntersectCapsuleAABB(float3 p0, float3 p1, float radius, float3 boxCenter, float3 boxExtents, out float3 intersectionDirection)
{
    const float EPSILON = 1e-6;
    float3 closestPoint = CalculateClosestPointToCapsule(boxCenter, p0, p1);

    float maxBoxExtents = max(max(boxExtents.x, boxExtents.y), boxExtents.z);
    
    //check intersection, TODO: do properly
    {
        float3 closestPointToCenter = boxCenter - closestPoint;
        float distSqrToClosestPoint = dot(closestPointToCenter, closestPointToCenter);
        float3 maxBoxExtendsAndRadius = maxBoxExtents + radius;

        //check if sphere containing the box extents is colliding, if not, early out
        if (distSqrToClosestPoint > dot(maxBoxExtendsAndRadius, maxBoxExtendsAndRadius))
        {
            intersectionDirection = 0;
            return false;
        }
        
        float vLen = length(closestPointToCenter);
        if(vLen > EPSILON)
        {
            closestPointToCenter /= vLen;
        }
        
        //move closest point from the capsules center to its hull (but not further than tile center). 
        closestPoint += closestPointToCenter * min(vLen, radius);

        intersectionDirection = closestPointToCenter * radius;

        float3 boxCornerMin = boxCenter - boxExtents;
        float3 boxCornerMax = boxCenter + boxExtents;
        
        if (all(boxCornerMin <= closestPoint) && all(boxCornerMax >= closestPoint))
        {
            return true;
        }
    }
    return false;
}

bool IntersectCapsuleSphere(float3 capsuleP0, float3 capsuleP1, float capsuleRadius, float4 sphere, out float3 intersectionOffset, out float normalizedDistFromCapsuleCenter)
{
    const float EPSILON = 1e-6;
    float3 closestPoint = CalculateClosestPointToCapsule(sphere.xyz, capsuleP0, capsuleP1);

    //check intersection
    {
        float3 closestPointToCenter = sphere.xyz - closestPoint;
        float distSqrToClosestPoint = dot(closestPointToCenter, closestPointToCenter);
        float combinedRadius = sphere.w + capsuleRadius;
        
        if (distSqrToClosestPoint > combinedRadius * combinedRadius)
        {
            intersectionOffset = 0;
            return false;
        }
        
        float vLen = length(closestPointToCenter);
        if(vLen > EPSILON)
        {
            closestPointToCenter /= vLen;
            
        }
        
        normalizedDistFromCapsuleCenter = vLen / combinedRadius;
        intersectionOffset = closestPointToCenter * combinedRadius;
    }
    return true;
}

/*
void ApplyLinearForce(float3 force, float dt, inout ScatteredInstanceState state)
{
    //do simple semi-implicit euler
    float3 r = state.offset;

    float damping = min(state.damping * dt, 1.f) / dt; //clamp damping to damp at maximum the current velocity
    
    float3 vLinearPrevious =  cross(state.velocity, r);
    float3 fLinear =  force - damping * vLinearPrevious;
    float3 vLinear = fLinear * dt;
    float3 vAngular = cross(r, vLinear);
    state.velocity = state.velocity + vAngular;
    state.offset = state.offset + cross(state.velocity, r) * dt;
}*/

void ApplyLinearForce(float3 force, float dt, inout ScatteredInstanceState state)
{
    
    float damping = min(state.damping * dt, 1.f) / dt; //clamp damping to damp at maximum the current velocity
    
    float3 vLinearPrevious =  state.velocity;
    float3 fLinear =  force - damping * vLinearPrevious;
    float3 vLinear = fLinear * dt;
    state.velocity = state.velocity + vLinear;
    state.offset = state.offset + state.velocity * dt;
}

void StepSimulation(inout ScatteredInstanceState state, float dt)
{

    {
        //try to maintain length
        float lenTo = length(state.equilibrium);
        float lenFrom = length(state.offset);
        float lenDiff = lenTo - lenFrom;
        float3 fToLength = (state.offset / lenFrom) * lenDiff * 4.f;
        ApplyLinearForce(fToLength * state.stiffness, dt, state);
    }

    {
        //try to go back to equilibrium
        float3 fToEquilibrium = state.equilibrium - state.offset;
        ApplyLinearForce(fToEquilibrium * state.stiffness, dt, state);
    }
    
    

}


#endif//__SCATTEREDINSTANCEINTERSECTIONUTILITY_HLSL__
