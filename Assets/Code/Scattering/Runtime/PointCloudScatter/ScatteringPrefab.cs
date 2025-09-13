using System.Collections;
using System.Collections.Generic;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;

namespace TimeGhost
{
    public class ScatteringPrefab : MonoBehaviour
    {
        public bool NeedsMotionVectors = true;
        public class ScatteringPrefabBaker : Baker<ScatteringPrefab>
        {
            
            public override void Bake(ScatteringPrefab authoring)
            {
                var entity = GetEntity(TransformUsageFlags.None);
                AddComponent(entity, new ScatteredInstanceExtraData());
                if (authoring.NeedsMotionVectors)
                {
                    AddComponent<NeedsMotionVectorsTag>(entity);
                }
            }
        }
    }
}