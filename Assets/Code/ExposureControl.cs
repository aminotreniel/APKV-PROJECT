using System;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;
using Object = UnityEngine.Object;

namespace TimeGhost
{
    [ExecuteAlways]
    [DisallowMultipleComponent]
    public class ExposureControl : MonoBehaviour
    {
        [Header("Fixed exposure override volume")]
        public int m_VolumePriority = 100;
        [Tooltip("Overrides the Fixed Exposure property")]
        public float m_FixedExposure = 10;
        
        [Header("Debug")]
        [Tooltip("Enabling debug will leave the backing data visible and editable in the scene.")]
        public bool m_Debug;

        GameObject m_Volume;
        VolumeProfile m_Profile;
        Exposure m_Exposure;
        
        HideFlags kHideFlags => m_Debug ? HideFlags.None : HideFlags.NotEditable | HideFlags.DontSaveInBuild | HideFlags.DontSaveInEditor | HideFlags.HideInHierarchy | HideFlags.HideInInspector;

        private void OnEnable()
        {
            m_Volume = new GameObject("ExposureControlVolume") {hideFlags = kHideFlags};
            var volume = m_Volume.AddComponent<Volume>();
            volume.isGlobal = true;
            volume.priority = m_VolumePriority;

            m_Profile = volume.sharedProfile = ScriptableObject.CreateInstance<VolumeProfile>();
            m_Profile.hideFlags = kHideFlags;
            m_Profile.name = "FixedExposureOverride";

            m_Exposure = m_Profile.Add<Exposure>();
            m_Exposure.hideFlags = kHideFlags;
            m_Exposure.mode.overrideState = true;
            m_Exposure.mode.value = ExposureMode.Fixed;
            m_Exposure.fixedExposure.overrideState = true;
            m_Exposure.active = true;
        }

        void OnDisable()
        {
            void SafeDestroy(Object obj) { if (Application.isPlaying) Destroy(obj); else DestroyImmediate(obj); }
        
            m_Profile.Remove<Exposure>();
            SafeDestroy(m_Volume);
            SafeDestroy(m_Profile);
            SafeDestroy(m_Exposure);
        }

        private void LateUpdate()
        {
            m_Exposure.fixedExposure.value = m_FixedExposure;
        }
    }
}
