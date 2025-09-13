using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;
using UnityEngine;
using CapsuleCollider = UnityEngine.CapsuleCollider;

namespace TimeGhost
{
    public class ScatteredInstanceColliderInjector : MonoBehaviour
    {
        private Entity m_Entity;
        private CapsuleCollider m_Capsule;

        private void OnEnable()
        {
            if (World.DefaultGameObjectInjectionWorld == null) return;
            
            if (TryGetComponent(out m_Capsule))
            {
                m_Entity = SpawnEntity(m_Capsule);
            }
        }

        private void OnDisable()
        {
            if (World.DefaultGameObjectInjectionWorld == null) return;
            
            if (m_Entity != Entity.Null)
            {
                EntityManager entityManager = World.DefaultGameObjectInjectionWorld.EntityManager;
                entityManager.DestroyEntity(m_Entity);
                m_Entity = Entity.Null;
            }
        }

        // Update is called once per frame
        void Update()
        {
            if (World.DefaultGameObjectInjectionWorld == null || m_Entity == Entity.Null) return;
            InjectColliderData(m_Capsule, m_Entity);


        }

        Entity SpawnEntity(CapsuleCollider capsule)
        {
            EntityManager entityManager = World.DefaultGameObjectInjectionWorld.EntityManager;
            EntityArchetype archetype = entityManager.CreateArchetype(
                typeof(LocalToWorld),
                typeof(ScatteredInstanceColliderData)
            );
            Entity ent = entityManager.CreateEntity(archetype);
            InjectColliderData(capsule, ent);
            return ent;
        }

        void InjectColliderData(CapsuleCollider capsule, Entity ent)
        {
            EntityManager entityManager = World.DefaultGameObjectInjectionWorld.EntityManager;
            
            float height = capsule.height;
            float radius = capsule.radius;
            var center = (float3)capsule.center;
            float top = math.max(height * 0.5f - radius, 0.0f);
            float bottom = math.min(-height * 0.5f + radius, 0.0f);
            
            float3 p0 = center + new float3(0.0f, bottom, 0.0f);
            float3 p1 = center + new float3(0.0f, top, 0.0f);
            
           
            entityManager.SetComponentData(ent, new ScatteredInstanceColliderData{P0 = p0, P1 = p1, Radius = radius});
            entityManager.SetComponentData(ent, new LocalToWorld{ Value = transform.localToWorldMatrix});
        }
    }
}
