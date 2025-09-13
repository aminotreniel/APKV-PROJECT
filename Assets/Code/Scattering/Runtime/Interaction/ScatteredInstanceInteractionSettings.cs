using System;
using System.Collections;
using System.Collections.Generic;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;

namespace TimeGhost
{
    [ExecuteAlways]
    public class ScatteredInstanceInteractionSettings : MonoBehaviour
    {
        public float activeRadius = 10;
        public float colliderSmoothingMarginRatio = 0.01f;
        public bool prioritizeGameCamera = true;

        private void Update()
        {
            var numberOfInstances = FindObjectsByType<ScatteredInstanceInteractionSettings>(FindObjectsInactive.Exclude, FindObjectsSortMode.None).Length;
            if (numberOfInstances > 1)
            {
                Debug.LogWarning($"there is more than one active ScatteredInstanceInteractionSettings instance (I live in {gameObject.name})! This is incorrect and the resulting settings is arbitrarily one of these.");
            }
            if (Input.GetKeyUp(KeyCode.KeypadPlus))
            {
                if(activeRadius < 1000)
                    ++activeRadius;
            }

            if (Input.GetKeyUp(KeyCode.KeypadMinus))
            {
                if (activeRadius > 1)
                    --activeRadius;
            }
            
            var world = World.DefaultGameObjectInjectionWorld;
            if(world == null) return;
            
            var interactSystem = world.GetExistingSystem(typeof(ScatteredInstanceInteractionSystem));
            if (interactSystem != SystemHandle.Null)
            {
                var cData =
                    world.EntityManager
                        .GetComponentDataRW<ScatteredInstanceSpatialInteractionSettingsData>(interactSystem);

                cData.ValueRW.ActiveRadius = Mathf.Max(activeRadius, 0);
                cData.ValueRW.ColliderSmoothingMargin = colliderSmoothingMarginRatio;
                cData.ValueRW.PrioritizeGameCamera = prioritizeGameCamera;
            }
        }
    }
    
}
