using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using UnityEditor;
using UnityEngine;
using Hash128 = Unity.Entities.Hash128;

namespace TimeGhost
{
    public class TileImpostorAuthoringBaker : Baker<TileImpostorAuthoring>
    {

        public struct TileImpostorEntry
        {
            public Entity Prefab;
            public AABB Bounds;
        }
        
        public override void Bake(TileImpostorAuthoring authoring)
        {
            if (authoring.dataSet == null || authoring.dataSet.GeneratedImposters == null) return;

            DependsOn(authoring.dataSet);

            foreach (var impostor in authoring.dataSet.GeneratedImposters)
            {
                DependsOn(impostor.Prefab);
            }

            var generatedImpostors = authoring.dataSet.GeneratedImposters;

            List<TileImpostorEntry> validEntries = new List<TileImpostorEntry>();

            foreach (var impostor in generatedImpostors)
            {
                var prefab = impostor.Prefab;
                if (prefab == null) continue;
                RegisterPrefabForBaking(prefab);
                Entity prefabEntity = GetEntity(prefab, TransformUsageFlags.Renderable);
                if (prefabEntity == Entity.Null) continue;
                AABB bounds = impostor.ImpostorBounds;
                
                validEntries.Add(new TileImpostorEntry()
                {
                    Bounds = bounds,
                    Prefab = prefabEntity
                });
            }

            if (validEntries.Count == 0)
            {
                Debug.Log("Failed to bake any tile impostors, all entries invalid. Forgot to commit generated impostors?");
                return;
            }
            else
            {
                Debug.Log($"Baking {validEntries.Count} Tile Impostors");
            }

            Hash128 id = new Hash128(0, (uint)authoring.GetHashCode(), 0, (uint)DateTime.UtcNow.GetHashCode());
            
            var mainEntity = GetEntity(TransformUsageFlags.None);
            AddComponent(mainEntity, new TileImpostorSystem.TileImpostorDataHash()
            {
                Hash = id
            });
            
            var entriesBuffer = AddBuffer<TileImpostorSystem.TileImpostorDataEntry>(mainEntity);
            for(int i = 0; i < validEntries.Count; ++i)
            {
                TileImpostorSystem.TileImpostorDataEntry entry = new TileImpostorSystem.TileImpostorDataEntry()
                {
                    Prefab = validEntries[i].Prefab,
                    Bounds = validEntries[i].Bounds
                };
                entriesBuffer.Add(entry);
            }

        }
    }
}