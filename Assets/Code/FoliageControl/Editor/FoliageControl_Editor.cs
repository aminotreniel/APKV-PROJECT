using UnityEngine;
using UnityEditor;

namespace TimeGhost
{
    [CustomEditor(typeof(FoliageControl))]
    public class LevelScriptEditor : Editor
    {
        private FoliageControl _target;
        private string _foliageDebugModeTip;
        
        private static bool _showDebugSettings = true;
        private static bool _showWindSettings = true;
        private readonly string[] _options = {"Debug Off", "Display Foliage Breaking", "Display Instances", "Display Strands", "Display Wind Map (RGB)", "Display Wind Low Frequency Intensity Map (Red Channel)", "Display Wind High Frequency Intensity Map (Green Channel)", "Display Wind Angle Variation Map (Blue Channel)", "Display Health"};
        
        void OnEnable()
        {
            _target = (FoliageControl)base.target;
        }
        
        public override void OnInspectorGUI()
        {
            _showWindSettings = EditorGUILayout.Foldout(_showWindSettings , "Wind Settings");
            if(_showWindSettings) 
            {
                EditorGUILayout.PropertyField(serializedObject.FindProperty("globalFoliageWindIntensity"), new GUIContent("Wind Intensity"));
                EditorGUILayout.Space();
                
                EditorGUILayout.PropertyField(serializedObject.FindProperty("globalFoliageWindAngle"), new GUIContent("Wind Direction"));
                EditorGUILayout.PropertyField(serializedObject.FindProperty("globalFoliageWindAngleVariation"), new GUIContent("Wind Direction Variation"));
                EditorGUILayout.Space();
                
                EditorGUILayout.PropertyField(serializedObject.FindProperty("globalFoliageWindSpeed"), new GUIContent("Wind Speed"));
                EditorGUILayout.PropertyField(serializedObject.FindProperty("globalFoliageWindSpeedVariationPerStrand"), new GUIContent("Wind Speed Variation Per Strand"));
                EditorGUILayout.Space();
                
                EditorGUILayout.PropertyField(serializedObject.FindProperty("globalFoliageWindLowFrequencyInfluence"), new GUIContent("Wind Low Frequency Influence"));
                EditorGUILayout.PropertyField(serializedObject.FindProperty("globalFoliageWindHighFrequencyInfluence"), new GUIContent("Wind High Frequency Influence"));
                EditorGUILayout.PropertyField(serializedObject.FindProperty("globalFoliageWindWavingSpeedMultiplier"), new GUIContent("Wind Waving Speed Multiplier"));
                EditorGUILayout.Space();
                
                EditorGUILayout.PropertyField(serializedObject.FindProperty("globalFoliageWindPatternMap"), new GUIContent("Wind Pattern Map (RGB)"));
                EditorGUILayout.HelpBox("Red: wind low frequency intensity variation\nGreen: wind high frequency intensity variation\nBlue: wind direction variation\n\nHigh or no compression recommended !", MessageType.Info);
                EditorGUILayout.PropertyField(serializedObject.FindProperty("globalFoliageWindPatternMapTiling"), new GUIContent("Wind Pattern Map Tiling"));
            }
            
            _showDebugSettings = EditorGUILayout.Foldout(_showDebugSettings , "Foliage Debug Settings");
            if(_showDebugSettings) 
            {
                var enabledKeywords2 = Shader.enabledGlobalKeywords;
                
                EditorGUILayout.PropertyField(serializedObject.FindProperty("globalFoliageDebugDisableWind"), new GUIContent("Disable Wind"));
                
                EditorGUI.BeginDisabledGroup(_target.globalFoliageDebugDisableWind);
                EditorGUILayout.PropertyField(serializedObject.FindProperty("globalFoliageDebugDisableLFBendByWind"), new GUIContent("Disable LF Bend By Wind"));
                EditorGUILayout.PropertyField(serializedObject.FindProperty("globalFoliageDebugDisableHFBendByWind"), new GUIContent("Disable HF Bend By Wind"));
                EditorGUILayout.PropertyField(serializedObject.FindProperty("globalFoliageDebugDisableWaveByWind"), new GUIContent("Disable Wave By Wind"));
                EditorGUI.EndDisabledGroup();
                EditorGUILayout.Space();
                
                EditorGUILayout.PropertyField(serializedObject.FindProperty("globalFoliageDebugOverrideHealth"), new GUIContent("Override Health"));
                EditorGUI.BeginDisabledGroup(_target.globalFoliageDebugOverrideHealth==false);
                EditorGUILayout.PropertyField(serializedObject.FindProperty("globalFoliageDebugNewHealth"), new GUIContent("New Health"));
                EditorGUI.EndDisabledGroup();
                EditorGUILayout.Space();
                
                //Shader.DisableKeyword("_GLOBALFOLIAGEDEBUG_A");
                //Shader.DisableKeyword("_GLOBALFOLIAGEDEBUG_B");
                //Shader.DisableKeyword("_GLOBALFOLIAGEDEBUG_C");
                //Shader.DisableKeyword("_GLOBALFOLIAGEDEBUG_D");
                //Shader.DisableKeyword("_GLOBALFOLIAGEDEBUG_E");
                
                //there's an issue here where this doesn't apply after reload, until the user selects the GO that has this script attached
                _target.globalFoliageDebugOptions = EditorGUILayout.Popup(_target.globalFoliageDebugOptions, _options);
                _target.FoliageDebugMode();

                _foliageDebugModeTip = _target.globalFoliageDebugOptions switch
                {
                    0 =>
                        //_target.FoliageDebugMode(0);
                        "No debug mode",
                    1 =>
                        //_target.FoliageDebugMode(1);
                        "Display breakage (red: broken, green: intact)",
                    2 =>
                        //_target.FoliageDebugMode(2);
                        "Display scattered instances",
                    3 =>
                        //_target.FoliageDebugMode(3);
                        "Display individual strands based on the red vertex color",
                    4 =>
                        //_target.FoliageDebugMode(4);
                        "Display the full RGB wind map with speed and angle",
                    5 =>
                        //_target.FoliageDebugMode(5);
                        "Display the wind low frequency intensity map (red channel)",
                    6 =>
                        //_target.FoliageDebugMode(6);
                        "Display the wind high frequency map (green channel)",
                    7 =>
                        //_target.FoliageDebugMode(7);
                        "Display the wind direction variation map (blue channel: lerp between positive & negative angle variation)",
                    8 =>
                        //_target.FoliageDebugMode(7);
                        "Display the health for each instance (green: healthy, red: sick)",
                    
                    _ => _foliageDebugModeTip
                };

                var enabledKeywords = Shader.enabledGlobalKeywords;
                //EditorGUILayout.PropertyField(serializedObject.FindProperty("globalFoliageDebugMode"), new GUIContent("Debug Foliage Mode"));
                
                EditorGUILayout.HelpBox(_foliageDebugModeTip, MessageType.Info);
            }
            
            serializedObject.ApplyModifiedProperties();
        }
    }
}
