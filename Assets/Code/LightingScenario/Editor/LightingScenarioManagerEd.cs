using UnityEngine;
using UnityEditor;

namespace TimeGhost
{
    [CustomEditor(typeof(LightingScenarioManager))]
    public class LightingScenarioManagerEd : Editor
    {
        new LightingScenarioManager target => (LightingScenarioManager)base.target;
		
        public override void OnInspectorGUI()
        {
            var spScenarios = serializedObject.FindProperty("scenarios");
            var scenarioCount = spScenarios.arraySize;
            
            var spScenarioBlend = serializedObject.FindProperty("scenarioBlend");
            
            var floatValue = spScenarioBlend.floatValue;
            var intValue = Mathf.FloorToInt(spScenarioBlend.floatValue);

            using (var ccs = new EditorGUI.ChangeCheckScope())
            {
                intValue = EditorGUILayout.IntSlider("Select Scenario", intValue, 0, scenarioCount - 1);

                if (ccs.changed)
                    spScenarioBlend.floatValue = intValue;
            }

            using (var ccs = new EditorGUI.ChangeCheckScope())
            {
                floatValue = EditorGUILayout.Slider("Blend Scenario", floatValue, 0, scenarioCount - 1);

                if (ccs.changed)
                    spScenarioBlend.floatValue = floatValue;
            }

            EditorGUILayout.Space();
            
            serializedObject.ApplyModifiedProperties();
            
            if (GUILayout.Button("Capture Scenario"))
            {
                target.CaptureScenario();
            }

            EditorGUILayout.Space();
            EditorGUILayout.Space();
            
            base.OnInspectorGUI();
        }
    }
}
