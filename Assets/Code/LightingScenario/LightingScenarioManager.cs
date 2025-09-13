using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Playables;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;
using UnityEngine.Timeline;

namespace TimeGhost
{
    [ExecuteAlways]
    public class LightingScenarioManager : MonoBehaviour, IPropertyPreview
    {
        [System.Serializable]
        struct Scenario
        {
            public string Name;

            public GameObject GlobalRoot;
            public GameObject SunRoot;
            public GameObject VolumesRoot;
            public GameObject ReflectionProbesRoot;

            public HDAdditionalLightData TargetSun;
            public Vector3 SunAngles;
            public float SunIntensity;
            public float SunTemperature;
            public Color SunColor;
            
            public Volume[] Volumes;
            public HDProbe[] ReflectionProbes;
            
            [System.Serializable]
            public struct ScenarioBakingSetup
            {
                public enum SkyUniqueType { None, PhysicallyBased, HDRI }
                public enum CloudUniqueType { None, CloudLayer }
                
                public bool UseBakingSetup;
                public VolumeProfile Profile;
                public SkyUniqueType SkyType;
                public CloudUniqueType CloudType;
                public bool UseVolumetricClouds;
            }

            public ScenarioBakingSetup BakingSetup;
        }

        [SerializeField] float scenarioBlend;
        [SerializeField] bool isTimelineDriven;
        [SerializeField] HDAdditionalLightData targetSun;
        [SerializeField] Scenario[] scenarios = System.Array.Empty<Scenario>();

        float m_LastScenarioBlend;

        public float ScenarioBlend
        {
            get => scenarioBlend;
            set => scenarioBlend = value;
        }

        public float LastScenarioBlend => m_LastScenarioBlend;

        void OnEnable()
        {
            SetScenarioBlend(scenarioBlend, !Application.isPlaying);
        }

        void LateUpdate()
        {
            if (!isTimelineDriven)
            {
                SetScenarioBlend(scenarioBlend, false);
            }
        }

        public void ForceRefresh()
        {
            SetScenarioBlend(scenarioBlend, true);
        }

        public void SetScenario(string name)
        {
            //Debug.Log($"Setting scenario '{name}.");
            
            for(var i = 0; i < scenarios.Length; ++i)
            {
                if (scenarios[i].Name == name)
                {
                    SetScenario(i);
                    return;
                }
            }
            
            Debug.LogWarning($"No scenario '{name}' found.");
        }

        public void SetScenario(int index)
        {
            SetScenarioBlend(index, false);
            scenarioBlend = m_LastScenarioBlend;
        }

        public void SetIsTimelineDriven(bool enable)
        {
            isTimelineDriven = enable;
        }

        public void SetScenarioBlend(string name0, string name1, float blend)
        {
            if (blend == 0f)
            {
                SetScenario(name0);
                return;
            }

            // ReSharper disable once CompareOfFloatsByEqualityOperator
            if (blend == 1f)
            {
                SetScenario(name1);
                return;
            }

            int index0 = -1, index1 = -1;
            
            for(var i = 0; i < scenarios.Length; ++i)
            {
                if (scenarios[i].Name == name0)
                {
                    index0 = i;
                }
                
                if (scenarios[i].Name == name1)
                {
                    index1 = i;
                }
            }

            if (index0 < 0 || index1 < 0)
            {
                Debug.LogError($"Unable to find both scenarios in blend, expecting: {name0} <-> {name1} @ {blend}.");
                return;
            }

            if ((index0 + 1) != index1)
            {
                Debug.LogError($"Currently only immediately sequential scenarios can be blended. Found indices {index0} : {name0}, {index1} : {name1}.");
                return;
            }

            SetScenarioBlend(index0 + blend, false);
            scenarioBlend = m_LastScenarioBlend;
        }

        public void SetScenarioBlend(float blend, bool force)
        {
            if (scenarios.Length == 0)
                return;
            
            // ReSharper disable once CompareOfFloatsByEqualityOperator
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

            if (!scenario0.GlobalRoot.activeSelf)
            {
                ActivateScenario(index0);
            }

            if (!scenario1.GlobalRoot.activeSelf)
            {
                ActivateScenario(index1);
            }

            targetSun.transform.eulerAngles = Vector3.Lerp(scenario0.SunAngles, scenario1.SunAngles, blend);
            targetSun.intensity = Mathf.Lerp(scenario0.SunIntensity, scenario1.SunIntensity, blend);
            var color = Color.Lerp(scenario0.SunColor, scenario1.SunColor, blend);
            var temperature = Mathf.Lerp(scenario0.SunTemperature, scenario1.SunTemperature, blend);
            targetSun.SetColor(color, temperature > 0f ? temperature : -1f);
            
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
            //Debug.Log($"Activating {scenario.Name} ({index})");

            if (scenario.GlobalRoot)
            {
                scenario.GlobalRoot.SetActive(true);
            }
            
            if (scenario.SunRoot)
            {
                scenario.SunRoot.SetActive(true);
            }
            else if(targetSun != null)
            {
                targetSun.transform.eulerAngles = scenario.SunAngles;
                targetSun.intensity = scenario.SunIntensity;
                targetSun.SetColor(scenario.SunColor, scenario.SunTemperature > 0f ? scenario.SunTemperature : -1f);
            }

            if (scenario.VolumesRoot)
            {
                scenario.VolumesRoot.SetActive(true);
            // }
            // else
            // {
                foreach (var volume in scenario.Volumes)
                {
                    volume.weight = 1f;
                    volume.enabled = true;
                    volume.gameObject.SetActive(true);
                }
            }

            if (scenario.ReflectionProbesRoot)
            {
                scenario.ReflectionProbesRoot.SetActive(true);
            // }
            // else
            // {
                foreach (var reflectionProbe in scenario.ReflectionProbes)
                {
                    reflectionProbe.weight = 1f;
                    reflectionProbe.enabled = true;
                    reflectionProbe.gameObject.SetActive(true);
                }
            }
            
            ProbeReferenceVolume.instance.lightingScenario = scenario.Name;
            
#if UNITY_EDITOR
            if (scenario.BakingSetup.UseBakingSetup && !Application.isPlaying && gameObject.scene.isLoaded)
            {
                gameObject.scene.GetRootGameObjects(m_RootsList);
                
                foreach (var go in m_RootsList)
                {
                    if (go.TryGetComponent<StaticLightingSky>(out var sky))
                    {
                        var staticLightingSkyUniqueID = 0;
                        if (scenario.BakingSetup.SkyType == Scenario.ScenarioBakingSetup.SkyUniqueType.PhysicallyBased)
                            staticLightingSkyUniqueID = SkySettings.GetUniqueID(typeof(PhysicallyBasedSky));
                        else if (scenario.BakingSetup.SkyType == Scenario.ScenarioBakingSetup.SkyUniqueType.HDRI)
                            staticLightingSkyUniqueID = SkySettings.GetUniqueID(typeof(HDRISky));

                        var staticLightingCloudsUniqueID = 0;
                        if (scenario.BakingSetup.CloudType == Scenario.ScenarioBakingSetup.CloudUniqueType.CloudLayer)
                            staticLightingCloudsUniqueID = SkySettings.GetUniqueID(typeof(CloudLayer));
                        else
                            staticLightingCloudsUniqueID = 0;

                        if (sky.profile != scenario.BakingSetup.Profile ||
                            sky.staticLightingSkyUniqueID != staticLightingSkyUniqueID ||
                            sky.staticLightingCloudsUniqueID != staticLightingCloudsUniqueID)
                        {
                            //Debug.Log("Setting light baking environment.");
                        
                            sky.profile = scenario.BakingSetup.Profile;

                            var soSky = new UnityEditor.SerializedObject(sky);
                            soSky.FindProperty("m_StaticLightingVolumetricClouds").boolValue = scenario.BakingSetup.UseVolumetricClouds;
                            soSky.ApplyModifiedPropertiesWithoutUndo();

                            sky.staticLightingSkyUniqueID = staticLightingSkyUniqueID;
                            sky.staticLightingCloudsUniqueID = staticLightingCloudsUniqueID;

                            UnityEditor.EditorApplication.QueuePlayerLoopUpdate();
                        }

                        break;
                    }
                }

                m_RootsList.Clear();
            }
#endif
        }
        List<GameObject> m_RootsList = new(16);

        void DeactivateScenario(int index)
        {
            var scenario = scenarios[index];
            //Debug.Log($"Deactivating {scenario.Name} ({index})");

            if (scenario.GlobalRoot)
            {
                scenario.GlobalRoot.SetActive(false);
            }
            
            if (scenario.SunRoot)
            {
                scenario.SunRoot.SetActive(false);
            }
            
            if (scenario.VolumesRoot)
            {
                scenario.VolumesRoot.SetActive(false);
            }
            else
            {
                foreach (var volume in scenario.Volumes)
                {
                    volume.weight = 0f;
                    volume.enabled = true;
                    volume.gameObject.SetActive(false);
                }
            }

            if (scenario.ReflectionProbesRoot)
            {
                scenario.ReflectionProbesRoot.SetActive(false);
            }
            else
            {
                foreach (var reflectionProbe in scenario.ReflectionProbes)
                {
                    reflectionProbe.weight = 0f;
                    reflectionProbe.enabled = true;
                    reflectionProbe.gameObject.SetActive(false);
                }
            }
        }

        public void CaptureScenario()
        {
            var index = Mathf.RoundToInt(scenarioBlend);
        
            if (Mathf.Abs(scenarioBlend - index) > Mathf.Epsilon)
            {
                throw new UnityException("Unable to capture blended scenario. Please select a non-blended one.");
            }
            
            ref var scenario = ref scenarios[index];
            scenario.TargetSun = targetSun;
            scenario.SunAngles = targetSun.transform.eulerAngles;
            scenario.SunIntensity = targetSun.intensity;
            scenario.SunColor = targetSun.color;
            var light = targetSun.GetComponent<Light>();
            scenario.SunTemperature = light.useColorTemperature ? light.colorTemperature : -1f;
        
            if (scenario.VolumesRoot && scenario.VolumesRoot.gameObject.activeInHierarchy)
            {
                scenario.Volumes = scenario.VolumesRoot.GetComponentsInChildren<Volume>();
            }
        
            if (scenario.ReflectionProbesRoot && scenario.ReflectionProbesRoot.gameObject.activeInHierarchy)
            {
                scenario.ReflectionProbes = scenario.ReflectionProbesRoot.GetComponentsInChildren<HDProbe>();
            }
        }
        
        public void GatherProperties(PlayableDirector director, IPropertyCollector driver)
        {
#if !DISABLE_GATHER
            driver.AddFromName(this, "isTimelineDriven");
            driver.AddFromName(this, "scenarioBlend");
            
            if (targetSun)
            {
                driver.AddFromName(targetSun.transform, "m_LocalRotation");

                var light = targetSun.GetComponent<Light>();
                driver.AddFromName(light, "m_Intensity");
                driver.AddFromName(light, "m_Color");
                driver.AddFromName(light, "m_ColorTemperature");
                driver.AddFromName(light, "m_UseColorTemperature");
            }
        
            if (scenarios.Length > 0)
            {
                var scenario = scenarios[Mathf.RoundToInt(scenarioBlend)];
        
                foreach (var volume in scenario.Volumes)
                {
                    driver.AddFromName(volume, "weight");
                    driver.AddFromName(volume, "m_Enabled");
                    driver.AddFromName(volume.gameObject, "m_IsActive");
                }
                
                foreach (var probe in scenario.ReflectionProbes)
                {
                    driver.AddFromName(probe, "m_ProbeSettings.lighting.weight");
                    driver.AddFromName(probe, "m_Enabled");
                    driver.AddFromName(probe.gameObject, "m_IsActive");
                }
            }
#endif
        }
    }
}
