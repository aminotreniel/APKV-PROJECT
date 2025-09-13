using System;
using System.Collections;
using System.Collections.Generic;
using Unity.Entities;
using Unity.Entities.Graphics;
using Unity.Mathematics;
using Unity.Rendering;
using Unity.Transforms;
using UnityEngine;
using UnityEngine.Rendering;
using Random = UnityEngine.Random;

namespace TimeGhost
{
    public class ScatteredInstanceColliderTestSpawner : MonoBehaviour
    {
        public float rotationSpeed = 0;
        public int spawnCount = 10;
        public float areaSize = 100;

        public float scaleMin = 1;
        public float scaleMax = 5;
        
        private List<Tuple<GameObject,Entity>> m_SpawnedEntities = new List<Tuple<GameObject,Entity>>();
        
        void Start()
        {
            
            SpawnTestColliders();
        }

        private void Update()
        {
            if (World.DefaultGameObjectInjectionWorld == null) return;
            
            if (rotationSpeed > 0)
            {
                transform.rotation *= Quaternion.AngleAxis(rotationSpeed * Time.deltaTime, Vector3.up);
            }

            EntityManager entityManager = World.DefaultGameObjectInjectionWorld.EntityManager;
        
            foreach (var tuple in m_SpawnedEntities)
            {
                float3 pos = tuple.Item1.transform.position;
                quaternion orientation = tuple.Item1.transform.rotation;
                
                LocalTransform lt = entityManager.GetComponentData<LocalTransform>(tuple.Item2);
                lt.Position = pos;
                lt.Rotation = orientation;
                lt.Scale = tuple.Item1.transform.localScale.x;
                entityManager.SetComponentData(tuple.Item2, lt);
                
            }
        }

        private void SpawnTestColliders()
        {
            EntityManager entityManager = World.DefaultGameObjectInjectionWorld.EntityManager;

            EntityArchetype archetype = entityManager.CreateArchetype(
                typeof(LocalToWorld),
                typeof(LocalTransform),
                typeof(ScatteredInstanceColliderData)
            );

            
            
            for (int i = 0; i < spawnCount; ++i)
            {
                GameObject prim = GameObject.CreatePrimitive(PrimitiveType.Capsule);
                float2 pos = Random.insideUnitCircle * areaSize;
                float3 spawnLocation = new float3(pos.x, 0, pos.y);
                float scale = Random.Range(scaleMin, scaleMax);
                Quaternion rotation = Quaternion.AngleAxis(Random.Range(0, 180), Vector3.up) *
                                      Quaternion.AngleAxis(90, Vector3.right);
                var ent = SpawnCollider(archetype, spawnLocation, scale, rotation);
                prim.transform.SetLocalPositionAndRotation(spawnLocation, rotation);
                prim.transform.localScale = Vector3.one * scale;
                prim.transform.SetParent(transform, false);
                m_SpawnedEntities.Add(new Tuple<GameObject, Entity>(prim, ent));
            }
            
        }

        private Entity SpawnCollider(EntityArchetype archetype, float3 pos, float scale, Quaternion rotation)
        {
            EntityManager entityManager = World.DefaultGameObjectInjectionWorld.EntityManager;
            Entity testCollider = entityManager.CreateEntity(archetype);
           
            entityManager.SetComponentData(testCollider, new ScatteredInstanceColliderData{P0 = new float3(0.0f, -0.5f, 0.0f), P1 = new float3(0.0f, 0.5f, 0.0f), Radius = 0.5f});
            entityManager.SetComponentData(testCollider, new LocalTransform{ Position = pos, Rotation = rotation, Scale = scale});
            return testCollider;

        }

        
    }
}
