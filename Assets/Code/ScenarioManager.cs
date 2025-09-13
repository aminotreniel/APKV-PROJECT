using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

namespace TimeGhost
{
    [ExecuteAlways]
    public class ScenarioManager : MonoBehaviour
    {
        [System.Serializable]
        struct Scenario
        {
            public string Name;
            
            public Vector3 SunAngles;
            public float SunIntensity;
            public Color SunColor;
            
            public Volume[] Volumes;
            public HDProbe[] ReflectionProbes;
        }

        [SerializeField] float scenarioBlend;
        [SerializeField] HDAdditionalLightData targetSun;
        [SerializeField] Scenario[] scenarios;

        float m_LastScenarioBlend;

        void OnEnable()
        {
            SetScenarioBlend(scenarioBlend, !Application.isPlaying);
        }

        void LateUpdate()
        {
            SetScenarioBlend(scenarioBlend, false);
        }

        public void ForceRefresh()
        {
            SetScenarioBlend(scenarioBlend, true);
        }

        public void SetScenarioBlend(float blend, bool force)
        {
            if (m_LastScenarioBlend == blend && !force)
                return;

            blend = Mathf.Clamp(blend, 0f, scenarios.Length - 1f);
            
            var index0 = Mathf.FloorToInt(blend);
            var index1 = Mathf.CeilToInt(blend);
            var factor = blend % 1f;

            for (var i = 0; i < scenarios.Length; ++i)
            {
                if (i != index0 && i != index1)
                    DeactivateScenario(i);
            }
            
            if(index0 == index1)
                ActivateScenario(index0);
            else
                BlendScenarios(index0, index1, factor);

            m_LastScenarioBlend = blend;
        }

        void BlendScenarios(int index0, int index1, float blend)
        {
            var scenario0 = scenarios[index0];
            var scenario1 = scenarios[index1];

            targetSun.transform.eulerAngles = Vector3.Lerp(scenario0.SunAngles, scenario1.SunAngles, blend);
            targetSun.intensity = Mathf.Lerp(scenario0.SunIntensity, scenario1.SunIntensity, blend);
            targetSun.color = Color.Lerp(scenario0.SunColor, scenario1.SunColor, blend);
            
            foreach (var volume in scenario0.Volumes)
            {
                volume.weight = 1f - blend;
                volume.enabled = true;
                volume.gameObject.SetActive(volume.weight > 0f);
            }

            foreach (var volume in scenario1.Volumes)
            {
                volume.weight = blend;
                volume.enabled = true;
                volume.gameObject.SetActive(volume.weight > 0f);
            }

            foreach (var reflectionProbe in scenario0.ReflectionProbes)
            {
                reflectionProbe.weight = 1f - blend;
                reflectionProbe.enabled = true;
                reflectionProbe.gameObject.SetActive(reflectionProbe.weight > 0f);
            }

            foreach (var reflectionProbe in scenario1.ReflectionProbes)
            {
                reflectionProbe.weight = blend;
                reflectionProbe.enabled = true;
                reflectionProbe.gameObject.SetActive(reflectionProbe.weight > 0f);
            }
            
            ProbeReferenceVolume.instance.lightingScenario = scenario0.Name;
            ProbeReferenceVolume.instance.BlendLightingScenario(scenario1.Name, blend);
        }

        void ActivateScenario(int index)
        {
            var scenario = scenarios[index];
            
            targetSun.transform.eulerAngles = scenario.SunAngles;
            targetSun.intensity = scenario.SunIntensity;
            targetSun.color = scenario.SunColor;
            
            foreach (var volume in scenario.Volumes)
            {
                volume.weight = 1f;
                volume.enabled = true;
                volume.gameObject.SetActive(true);
            }

            foreach (var reflectionProbe in scenario.ReflectionProbes)
            {
                reflectionProbe.weight = 1f;
                reflectionProbe.enabled = true;
                reflectionProbe.gameObject.SetActive(true);
            }
            
            ProbeReferenceVolume.instance.lightingScenario = scenario.Name;
        }

        void DeactivateScenario(int index)
        {
            var scenario = scenarios[index];

            foreach (var volume in scenario.Volumes)
            {
                volume.weight = 0f;
                volume.enabled = true;
                volume.gameObject.SetActive(false);
            }

            foreach (var reflectionProbe in scenario.ReflectionProbes)
            {
                reflectionProbe.weight = 0f;
                reflectionProbe.enabled = true;
                reflectionProbe.gameObject.SetActive(false);
            }
        }
    }
    
#if UNITY_EDITOR
    [UnityEditor.CustomEditor(typeof(ScenarioManager))]
    public class ScenarioManagerEd : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            var spScenarios = serializedObject.FindProperty("scenarios");
            var scenarioCount = spScenarios.arraySize;
            
            var spScenarioBlend = serializedObject.FindProperty("scenarioBlend");
            
            var floatValue = spScenarioBlend.floatValue;
            var intValue = Mathf.FloorToInt(spScenarioBlend.floatValue);

            using (var ccs = new UnityEditor.EditorGUI.ChangeCheckScope())
            {
                intValue = UnityEditor.EditorGUILayout.IntSlider("Select Scenario", intValue, 0, scenarioCount - 1);

                if (ccs.changed)
                    spScenarioBlend.floatValue = intValue;
            }

            using (var ccs = new UnityEditor.EditorGUI.ChangeCheckScope())
            {
                floatValue = UnityEditor.EditorGUILayout.Slider("Blend Scenario", floatValue, 0, scenarioCount - 1);

                if (ccs.changed)
                    spScenarioBlend.floatValue = floatValue;
            }

            serializedObject.ApplyModifiedProperties();

            UnityEditor.EditorGUILayout.Space();
            UnityEditor.EditorGUILayout.Space();
            
            base.OnInspectorGUI();
        }
    }
#endif
}
