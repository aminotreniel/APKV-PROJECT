using System;
using System.Collections;
using System.Collections.Generic;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Serialization;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace TimeGhost
{
    [ExecuteAlways]
    public class PointCloudSystemConfig : MonoBehaviour
    {
        
        public int sceneSectionsMaxInstancesToLoadConcurrently = 500000;

        [Space]
        public int maxNumberOfInstancesToSpawnPerBatch = 500000;
        public bool disableScatterWhileLoadingPrefabs = true;
        public bool immediateBlockingScatterOnStartup = false;
        
        [Space]
        public float scatterInstanceSizeMinMaxInterpolation = 0.5f;
        public float targetSizeOnScreen = 0.01f;
        [Range(0.0f, 0.4f)] 
        public float unloadSizeMarginRatio = 0.05f;
        
        [FormerlySerializedAs("tileImpostorDistance")] [Space]
        public float tileImpostorLOD0Distance = 10000;
        [FormerlySerializedAs("tileImpostorCullDistanceFar")] public float tileImpostorLOD1Distance = 20000;
        public float tileImpostorCullDistance = 30000;
        public float tileImpostorDistanceFade = 0;

        [Space]
        public float shadowCullScreenSizePercentage = 5.0f;
        public float shadowCullScreenSizeVariance = 2.0f;
        public bool cullShadowsOutsideCamera = true;
        public float cullShadowsCameraPlaneMargin = 0.0f;
        public int maxShadowCasterChunkChangesPerFrame = 20000;
        
        [Space]
        public int lodTransitionConstantOffset = 0;
        public float lodTransitionMultiplier = 1.0f;
        public bool lodTransitionMultiplierAffectsCullingDistance = true;
        [Space]
        [Range(0.0f, 1.0f)]
        public float alwaysVisibleScreenSize = 0.0f;
        [Range(0.0f, 1.0f)]
        public float neverVisibleScreenSize = 0.0f;
        [Range(0.01f, 100.0f)]
        public float screenSizeCullingEasing = 1.0f;

        public bool forceConstantEditorUpdate = true;

        [Space]
        [Range(0.0f, 1.0f)]
        public float drawLODDebugColors = 0.0f;
        public Color[] debugColors = new Color[] { Color.green, Color.red, Color.blue, Color.yellow, Color.magenta, Color.black, Color.white };
        
        [NonSerialized]
        private int framesToForceBlockingScatter ;

        private const float c_FlattenHeightDifference = 0;
        private void Start()
        {
            framesToForceBlockingScatter = 0;
            if (Application.isPlaying && immediateBlockingScatterOnStartup)
            {
                framesToForceBlockingScatter = 30;
            }
        }

        // Update is called once per frame
        void Update()
        {
            var world = World.DefaultGameObjectInjectionWorld;

            if (world == null) return;

            if (framesToForceBlockingScatter > 0)
            {
                --framesToForceBlockingScatter;
            }

            //ScatterPointCloudSystem
            {
                var pcSystem = world.GetExistingSystemManaged<ScatterPointCloudSystem>();
                if (pcSystem != null)
                {
                    var cData = world.EntityManager.GetComponentDataRW<PointCloudSystemConfigData>(pcSystem.SystemHandle);
                    cData.ValueRW.MaxNumberOfSpawnedInstancesPerBatch = maxNumberOfInstancesToSpawnPerBatch;
                    cData.ValueRW.DisableScatteringWhilePrefabsLoading = disableScatterWhileLoadingPrefabs;
                    cData.ValueRW.ImmediateBlockingScatter = framesToForceBlockingScatter > 0;
                    cData.ValueRW.InstanceModelSizeInterp = scatterInstanceSizeMinMaxInterpolation;
                    cData.ValueRW.InstanceTargetSizeOnScreen = targetSizeOnScreen;
                    cData.ValueRW.InstanceUnloadTargetSizeMargin = unloadSizeMarginRatio;
                    cData.ValueRW.MaxVisibleDistance = tileImpostorLOD0Distance + tileImpostorDistanceFade;
                    cData.ValueRW.FlattenHeightDifferenceMultiplier = 1;
                }
            }
            
            //SceneLoader
            {
                var sys = world.GetExistingSystem<ScatterSceneLoaderSystem>();
                if (sys != SystemHandle.Null)
                {
                    var cData = world.EntityManager.GetComponentDataRW<ScatterSceneLoaderSystem.ScatterSceneLoaderSystemConfigData>(sys);
                    cData.ValueRW.BlockOnLoad = framesToForceBlockingScatter > 0;
                    cData.ValueRW.MaxInstancesToQueue = sceneSectionsMaxInstancesToLoadConcurrently;
                    cData.ValueRW.FlattenHeightDifferenceMultiplier = c_FlattenHeightDifference;
                }
            }

            //Camera Tracking
            {
                var sys = world.GetExistingSystem<CameraTrackingSystem>();
                if (sys != SystemHandle.Null)
                {
                    var cData = world.EntityManager.GetComponentDataRW<CameraTrackingSystem.CameraTrackingData>(sys);
                    cData.ValueRW.forceConstantUpdate = forceConstantEditorUpdate;
                }
            }
            
            //TileImpostor
            {
                var sys = world.GetExistingSystem<TileImpostorSystem>();
                if (sys != SystemHandle.Null)
                {
                    var cData = world.EntityManager.GetComponentDataRW<TileImpostorSystem.TileImpostorSystemConfigData>(sys);
                    cData.ValueRW.ImpostorLOD0Distance = tileImpostorLOD0Distance;
                    cData.ValueRW.ImpostorLOD1Distance = math.max(tileImpostorLOD1Distance, tileImpostorLOD0Distance);
                    cData.ValueRW.ImpostorCullDistance = math.max(cData.ValueRW.ImpostorLOD1Distance + tileImpostorDistanceFade + 1, tileImpostorCullDistance);
                    cData.ValueRW.ImpostorFadeDistance = tileImpostorDistanceFade;
                    cData.ValueRW.FlattenHeightDifferenceMultiplier = 1;
                }
            }
            
            //LOD System
            {
                var sys = world.GetExistingSystemManaged<ScatterLODSystem>();
                if (sys != null)
                {
                    var cData = world.EntityManager.GetComponentDataRW<ScatterLODSystemConfigData>(sys.SystemHandle);
                    cData.ValueRW.DrawLODDebugColors = drawLODDebugColors;
                    cData.ValueRW.ShadowCullScreenSize = shadowCullScreenSizePercentage * 0.01f;
                    cData.ValueRW.InstanceSizeRangeInterpolateParam = scatterInstanceSizeMinMaxInterpolation;
                    cData.ValueRW.ShadowCullScreenSizeVariation = shadowCullScreenSizeVariance * 0.01f;
                    cData.ValueRW.LODTransitionConstantOffset = lodTransitionConstantOffset;
                    cData.ValueRW.LODTransitionMultiplier = lodTransitionMultiplier;
                    cData.ValueRW.LODTransitionMultiplierAffectsCulling = lodTransitionMultiplierAffectsCullingDistance;
                    cData.ValueRW.ShadowCullFrustrumPlaneMargin = cullShadowsCameraPlaneMargin;
                    cData.ValueRW.ShadowCullOutsideCamera = cullShadowsOutsideCamera;

                    cData.ValueRW.AlwaysVisibleScreenSize = alwaysVisibleScreenSize;
                    cData.ValueRW.NeverVisibleScreenSize = math.min(neverVisibleScreenSize, alwaysVisibleScreenSize);
                    cData.ValueRW.ScreenSizeCullingEasing = screenSizeCullingEasing;
                    cData.ValueRW.MaxShadowCasterChunkChangesPerFrame = maxShadowCasterChunkChangesPerFrame;
                    cData.ValueRW.MaxVisibilityDistance = tileImpostorLOD0Distance + tileImpostorDistanceFade;

                    {
                        cData.ValueRW.DebugColors.Clear();
                        foreach (var col in debugColors)
                        {
                            cData.ValueRW.DebugColors.Add(col);
                        }
                    }
                    
                }
            }
        }
    }
}
