using System;
using UnityEngine;

namespace TimeGhost
{
    [ExecuteAlways]
    public class ScatterQualitySettingsListener : MonoBehaviour
    {
        private PointCloudSystemConfig m_TargetConfig;
        
        
        private void OnEnable()
        {
            gameObject.TryGetComponent(out m_TargetConfig);
        }


        private void Update()
        {
            if(m_TargetConfig == null) return;

            var settings = ActiveScatterQualitySettings.Inst.Settings;
            m_TargetConfig.tileImpostorLOD0Distance = settings.ScatterDistance;
            m_TargetConfig.tileImpostorLOD1Distance = settings.TileImpostorLOD0VisibilityDistance;
            m_TargetConfig.tileImpostorCullDistance = settings.TileImpostorLOD1VisibilityDistance;
            m_TargetConfig.alwaysVisibleScreenSize = settings.ScreenSizeVisibilityAlways;
            m_TargetConfig.neverVisibleScreenSize = settings.ScreenSizeVisibilityNever;
            m_TargetConfig.shadowCullScreenSizePercentage = settings.ShadowCullSize;
            m_TargetConfig.lodTransitionMultiplier = settings.LODMultiplier;
            m_TargetConfig.lodTransitionConstantOffset = settings.LODBias;
            m_TargetConfig.tileImpostorDistanceFade = settings.ScatterFadeDistance;
            m_TargetConfig.maxNumberOfInstancesToSpawnPerBatch = settings.MaxInstancesToInstantiatePerBatch;
            
        }
    }
}