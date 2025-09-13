using System.Collections.Generic;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
#if UNITY_EDITOR
using UnityEditor;
#endif
using UnityEngine;
using UnityEngine.Rendering;

namespace TimeGhost
{
    
    
    [UpdateInGroup(typeof(InitializationSystemGroup))]
    [WorldSystemFilter(WorldSystemFilterFlags.Default | WorldSystemFilterFlags.Editor)]
    public partial class CameraTrackingSystem : SystemBase
    {
        private static readonly float s_ForceApplicationTickCameraMovementThreshold = 1;
        private static readonly float s_ForceApplicationTickCameraDirectionThreshold = 0.9f;

        private float3 m_LastCameraUpdatePosition;
        private float3 m_LastCameraUpdateDirection;
        public struct CameraTrackingData : IComponentData
        {
            public CameraType lastDataCameraType;
            public bool hasValidCameraDataThisFrame;
            public float3 CameraPosition;
            public NativeArray<float4> CameraFrustrumPlanes;
            public float TanFOV;
            public bool forceConstantUpdate;
        }

        void OnBeginContextRendering(ScriptableRenderContext context, List<Camera> cameras)
        {
            
            Camera cameratoFollow = null;
            //for now just pick scene camera if available. Otherwise game view (for testing)
            foreach (var cam in cameras)
            {
                if (cam.cameraType == CameraType.Game)
                {
                    cameratoFollow = cam;
                }

                if (cam.cameraType == CameraType.SceneView)
                {
                    if (cameratoFollow == null)
                    {
                        cameratoFollow = cam;
                    }
                }
            }
            
            if (cameratoFollow != null)
            {
                var cameraTrackingSystem = World.GetExistingSystemManaged<CameraTrackingSystem>();
                if (cameraTrackingSystem != null)
                {
                    var cData = EntityManager.GetComponentDataRW<CameraTrackingData>(SystemHandle);

                    if (cData.ValueRO.hasValidCameraDataThisFrame)
                    {
                        if (cData.ValueRO.lastDataCameraType == CameraType.Game)
                        {
                            return;
                        }
                    }
                    
                    float3 camPos = cameratoFollow.transform.position;
                    float3 camDir = cameratoFollow.transform.forward;
                    
                    #if UNITY_EDITOR
                    bool forceUpdateCamera = cData.ValueRO.forceConstantUpdate;
                    if (math.lengthsq(m_LastCameraUpdatePosition - camPos) > (s_ForceApplicationTickCameraMovementThreshold * s_ForceApplicationTickCameraMovementThreshold) || math.dot(camDir, m_LastCameraUpdateDirection) < s_ForceApplicationTickCameraDirectionThreshold || forceUpdateCamera)
                    {
                        EditorApplication.QueuePlayerLoopUpdate();
                        m_LastCameraUpdatePosition = camPos;
                        m_LastCameraUpdateDirection = camDir;
                    }
                    #endif
                    
                    var planes = cData.ValueRO.CameraFrustrumPlanes;
                    Unity.Rendering.FrustumPlanes.FromCamera(cameratoFollow, planes);
                    cData.ValueRW.CameraPosition = camPos;
                    cData.ValueRW.TanFOV = math.tan(math.TORADIANS * cameratoFollow.fieldOfView * 0.5f) * 2.0f;
                    cData.ValueRW.lastDataCameraType = cameratoFollow.cameraType;
                    cData.ValueRW.hasValidCameraDataThisFrame = true;
                }
                
            }
            
        }

        protected override void OnCreate()
        {
            RenderPipelineManager.beginContextRendering += OnBeginContextRendering;
            
            CameraTrackingData trackingData = new CameraTrackingData
            {
                CameraPosition = float3.zero,
                CameraFrustrumPlanes = new NativeArray<float4>(6, Allocator.Persistent),
                lastDataCameraType = CameraType.SceneView,
                hasValidCameraDataThisFrame = false
            };
            
            EntityManager.AddComponentData(SystemHandle, trackingData);
        }

        protected override void OnUpdate()
        {
            var cData = EntityManager.GetComponentDataRW<CameraTrackingData>(SystemHandle);
            cData.ValueRW.hasValidCameraDataThisFrame = false;
        }

        protected override void OnDestroy()
        {
            RenderPipelineManager.beginContextRendering -= OnBeginContextRendering;
            EntityManager.GetComponentData<CameraTrackingData>(SystemHandle).CameraFrustrumPlanes.Dispose();
        }
    }
}
