using TimeGhost;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;

namespace TimeGhost
{
    public class ScatteringPrefabInteractiveParametersBaker : Baker<ScatteringPrefabInteractiveParameters>
    {
        public override void Bake(ScatteringPrefabInteractiveParameters authoring)
        {
            var entity = GetEntity(TransformUsageFlags.None);
            AddComponent(entity, new ScatteredInstanceNeedsPartitioningTag());
            AddBuffer<ScatteredInstanceChildren>(entity);

            float3 springOffset = 0.0f;
            float springRadius = 0.1f;

            {
                Bounds bounds = new Bounds();
                bool boundsInitialized = false;
                if (authoring.TryGetComponent<LODGroup>(out LODGroup grp))
                {
                    LOD[] lods = grp.GetLODs();
                    foreach (var lod in lods)
                    {
                        var renderers = lod.renderers;
                        foreach (var rend in renderers)
                        {
                            if (boundsInitialized)
                            {
                                bounds.Encapsulate(rend.localBounds);
                            }
                            else
                            {
                                boundsInitialized = true;
                                bounds = rend.localBounds;
                            }
                        }
                    }
                }
                else if (authoring.TryGetComponent<Renderer>(out Renderer rend))
                {
                    bounds = rend.localBounds;
                    boundsInitialized = true;
                }

                if (boundsInitialized)
                {
                    //TODO: better way to calculate the spring parameters
                    float height = bounds.size.y;
                    float width = math.max(bounds.extents.x, bounds.extents.z);
                    springOffset = new float3(0.0f, math.max(height - width * 0.5f, 0.0001f), 0.0f);
                    springRadius = width;
                }
            }

            AddSharedComponent(entity, new ScatteredInstanceSpringData()
            {
                DampingMinMax = new float2(authoring.physicsDampingMin, authoring.physicsDampingMax),
                StiffnessMinMax = new float2(authoring.physicsStiffnessMin, authoring.physicsStiffnessMax),
                SpringTipRadius = springRadius,
                SpringTipOffset = springOffset,
                BreakingRecoveryAngleMinMax = new float2(authoring.physicsRecoveryAngleInDegreesMin * math.TORADIANS, authoring.physicsRecoveryAngleInDegreesMax * math.TORADIANS),
                BreakingAngleMinMax = new float2(authoring.physicsBreakingAngleInDegreesMin * math.TORADIANS, authoring.physicsBreakingAngleInDegreesMax * math.TORADIANS)
            });
        }
    }
}