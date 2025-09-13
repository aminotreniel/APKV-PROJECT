using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace TimeGhost
{
    [ExecuteInEditMode]
    public class FoliageControl : MonoBehaviour
    {
        [SerializeField, HideInInspector, Range(0, 1)]
        public float globalFoliageWindIntensity = 0.5f;

        [SerializeField, HideInInspector, Range(0, 1)]
        public float globalFoliageWindLowFrequencyInfluence = 1.0f;

        [SerializeField, HideInInspector, Range(0, 1)]
        public float globalFoliageWindHighFrequencyInfluence = 0.25f;
        
        [SerializeField, HideInInspector]
        public float globalFoliageWindWavingSpeedMultiplier = 1.0f;
        
        [SerializeField, HideInInspector]
        public Texture2D globalFoliageWindPatternMap;
        
        [SerializeField, HideInInspector]
        public float globalFoliageWindPatternMapTiling = 0.1f;
        
        [SerializeField, HideInInspector]
        public float globalFoliageWindSpeed = 0.1f;
        
        [SerializeField, HideInInspector, Range(0, 1)] 
        public float globalFoliageWindSpeedVariationPerStrand = 0.1f;

        [SerializeField, HideInInspector, Range (0, 360)] 
        public float globalFoliageWindAngle;
        
        [SerializeField, HideInInspector, Range (0, 360)] 
        public float globalFoliageWindAngleVariation = 45f;
        
        [SerializeField, HideInInspector]
        public bool globalFoliageDebugDisableWind;
        
        [SerializeField, HideInInspector]
        public bool globalFoliageDebugDisableLFBendByWind;
        
        [SerializeField, HideInInspector]
        public bool globalFoliageDebugDisableHFBendByWind;
        
        [SerializeField, HideInInspector]
        public bool globalFoliageDebugDisableWaveByWind;
        
        [SerializeField, HideInInspector]
        public bool globalFoliageDebugOverrideHealth;
        
        [SerializeField, HideInInspector, Range(0, 1)] 
        public float globalFoliageDebugNewHealth;

        [SerializeField, HideInInspector] 
        public int globalFoliageDebugOptions;
        
        private void FoliageWindControlSettings()
        {
            var texture = globalFoliageWindPatternMap as Texture;
            Shader.SetGlobalFloat(Shader.PropertyToID("_GlobalFoliageWindIntensity"), globalFoliageWindIntensity);
            
            Shader.SetGlobalFloat(Shader.PropertyToID("_GlobalFoliageWindLowFrequencyInfluence"), globalFoliageWindLowFrequencyInfluence);
            Shader.SetGlobalFloat(Shader.PropertyToID("_GlobalFoliageWindHighFrequencyInfluence"), globalFoliageWindHighFrequencyInfluence);
            Shader.SetGlobalFloat(Shader.PropertyToID("_GlobalFoliageWindWavingSpeedMultiplier"), globalFoliageWindWavingSpeedMultiplier);
            
            Shader.SetGlobalTexture(Shader.PropertyToID("_GlobalFoliageWindPatternMap"), globalFoliageWindPatternMap);
            Shader.SetGlobalFloat(Shader.PropertyToID("_GlobalFoliageWindPatternMapTiling"), globalFoliageWindPatternMapTiling);
            
            Shader.SetGlobalFloat(Shader.PropertyToID("_GlobalFoliageWindSpeed"), globalFoliageWindSpeed);
            Shader.SetGlobalFloat(Shader.PropertyToID("_GlobalFoliageWindSpeedVariationPerStrand"), globalFoliageWindSpeedVariationPerStrand);

            Shader.SetGlobalFloat(Shader.PropertyToID("_GlobalFoliageWindAngle"), globalFoliageWindAngle);
            Shader.SetGlobalFloat(Shader.PropertyToID("_GlobalFoliageWindAngleVariation"), globalFoliageWindAngleVariation);
        }

        public void FoliageDebugMode()
        {
            Shader.SetGlobalFloat(Shader.PropertyToID("_GlobalFoliageDebugMode"), globalFoliageDebugOptions);
        }

        private void FoliageDebug()
        {
            var globalFoliageDebugDisableWindFloat = globalFoliageDebugDisableWind ? 1f : 0f;
            Shader.SetGlobalFloat(Shader.PropertyToID("_GlobalFoliageDebugDisableWind"), globalFoliageDebugDisableWindFloat);
            
            var globalFoliageDebugDisableLFBendByWindFloat = globalFoliageDebugDisableLFBendByWind ? 1f : 0f;
            Shader.SetGlobalFloat(Shader.PropertyToID("_GlobalFoliageDebugDisableLFBendByWind"), globalFoliageDebugDisableLFBendByWindFloat);
            
            var globalFoliageDebugDisableHFBendByWindFloat = globalFoliageDebugDisableHFBendByWind ? 1f : 0f;
            Shader.SetGlobalFloat(Shader.PropertyToID("_GlobalFoliageDebugDisableHFBendByWind"), globalFoliageDebugDisableHFBendByWindFloat);
            
            var globalFoliageDebugDisableWaveByWindFloat = globalFoliageDebugDisableWaveByWind ? 1f : 0f;
            Shader.SetGlobalFloat(Shader.PropertyToID("_GlobalFoliageDebugDisableWaveByWind"), globalFoliageDebugDisableWaveByWindFloat);
            
            var globalFoliageDebugOverrideHealthFloat  = globalFoliageDebugOverrideHealth ? 1f : 0f;
            Shader.SetGlobalFloat(Shader.PropertyToID("_GlobalFoliageDebugOverrideHealth"), globalFoliageDebugOverrideHealthFloat);
            
            Shader.SetGlobalFloat(Shader.PropertyToID("_GlobalFoliageDebugNewHealth"), globalFoliageDebugNewHealth);
        }

        private void OnEnable()
        {
            FoliageWindControlSettings();
        }

        #if UNITY_EDITOR
        private void OnValidate() => UnityEditor.EditorApplication.delayCall += _OnValidate;
        private void _OnValidate()
        {
            UnityEditor.EditorApplication.delayCall -= _OnValidate;
            if (this == null) return;
            
            FoliageDebug();
            FoliageWindControlSettings();
        }
#endif
    }

}
