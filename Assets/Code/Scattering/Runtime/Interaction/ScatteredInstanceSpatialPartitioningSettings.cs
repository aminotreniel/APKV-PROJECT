using System;
using System.Collections;
using System.Collections.Generic;
using Unity.Entities;
using UnityEngine;

namespace TimeGhost
{
    [ExecuteAlways]
    public class ScatteredInstanceSpatialPartitioningSettings : MonoBehaviour
    {
        public float cellSizeInMeters = 1;

        private void Update()
        {
            var world = World.DefaultGameObjectInjectionWorld;
            if (world == null) return;
            
            var partSystem = world.GetExistingSystem(typeof(ScatteredInstanceSpatialPartitioningSystem));
            
            if (partSystem != SystemHandle.Null)
            {
                var cData =
                    world.EntityManager
                        .GetComponentDataRW<ScatteredInstanceSpatialPartitioningSettingsData>(partSystem);
                cData.ValueRW.CellSizeInMeters = cellSizeInMeters;
            }
        }
    }
}
