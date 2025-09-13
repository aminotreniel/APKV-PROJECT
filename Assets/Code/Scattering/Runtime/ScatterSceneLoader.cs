using System;
using System.Collections;
using System.Collections.Generic;
using Unity.Entities;
using Unity.Entities.Serialization;
using Unity.Scenes;
#if UNITY_EDITOR
using UnityEditor;
#endif
using UnityEngine;

namespace TimeGhost
{
    [ExecuteAlways]
    public class ScatterSceneLoader : MonoBehaviour
    {

        public SubScene[] ScenesToLoad;

        private Dictionary<SubScene, Entity> m_loadedScenes = new Dictionary<SubScene, Entity>();

        private void OnEnable()
        {
            if (ScenesToLoad == null) return;
            foreach (var sc in ScenesToLoad)
            {
                sc.AutoLoadScene = false;
            }
        }

        private void OnDisable()
        {
            var world = World.DefaultGameObjectInjectionWorld;
            if (world == null) return;
            foreach (var loadedScene in m_loadedScenes)
            {
                Unload(world, loadedScene.Value);
            }
        }

        private void Update()
        {
            var world = World.DefaultGameObjectInjectionWorld;

            if (world == null) return;
            
            if (ScenesToLoad == null) return;
            for (int i = 0; i < ScenesToLoad.Length; ++i)
            {
                SubScene sc = ScenesToLoad[i];
                if (sc == null || !sc.gameObject.activeInHierarchy)
                {
                    if (m_loadedScenes.TryGetValue(sc, out var entity))
                    {
                        Unload(world, entity);
                    }
                }
                else
                {
                    
                    if (m_loadedScenes.TryGetValue(sc, out Entity existingEntity))
                    {
                        var loadedState = SceneSystem.GetSceneStreamingState(world.Unmanaged, existingEntity);
                        bool isUnloaded = loadedState == SceneSystem.SceneStreamingState.Unloaded;
                        if (isUnloaded)
                        {
                            m_loadedScenes.Remove(sc);
                        }
                        else
                        {
                            return;
                        }
                    }
                    Entity ent = Load(world, sc);
                    
                    m_loadedScenes[sc] = ent;

                }
                
            }
        }

        private void Unload(World w, Entity ent)
        {
            w.EntityManager.DestroyEntity(ent);
        }

        private Entity Load(World w, SubScene sc)
        {
            var ent = w.EntityManager.CreateEntity();
            
            w.EntityManager.AddComponentData(ent, new ScatterSceneToLoad() { sceneReference = new EntitySceneReference(sc.SceneGUID, 0) });
            return ent;
        }
    }

    
}
