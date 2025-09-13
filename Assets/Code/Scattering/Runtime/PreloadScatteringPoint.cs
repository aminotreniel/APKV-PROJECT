using System.Collections.Generic;
using Unity.Entities;
using Unity.Entities.Serialization;
using Unity.Mathematics;
using Unity.Scenes;
using UnityEngine;

namespace TimeGhost
{
    [ExecuteAlways]
    public class PreloadScatteringPoint : MonoBehaviour
    {
        private Entity m_Entity = Entity.Null;
        private void OnEnable()
        {
            DestroyPreloadPoint();
            CreatePreloadPoint(transform.position);
        }

        private void OnDisable()
        {
            DestroyPreloadPoint();
        }

        void CreatePreloadPoint(float3 pos)
        {
            var world = World.DefaultGameObjectInjectionWorld;
            if (world == null) return;
            m_Entity = world.EntityManager.CreateEntity();
            world.EntityManager.AddComponentData(m_Entity, new ScatteringPrewarmPosition()
            {
                Value = pos
            });
        }

        void DestroyPreloadPoint()
        {
            
            var world = World.DefaultGameObjectInjectionWorld;
            if (world == null) return;
            if (m_Entity == Entity.Null) return;
            world.EntityManager.DestroyEntity(m_Entity);
            m_Entity = Entity.Null;
        }
        
    }
}