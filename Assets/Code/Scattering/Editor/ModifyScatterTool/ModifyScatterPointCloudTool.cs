using System.Collections.Generic;
using System.IO;
using Unity.Entities;
using Unity.Mathematics;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace TimeGhost
{
    public class ModifyScatterPointCloudTool : EditorWindow
    {
        [SerializeField] private ScatterPointCloudAuthoring[] m_Targets;
        private SerializedObject m_SerializedObject;

        private float m_OmitArea = 0.0001f;
        private float m_VisibleRadius = 50.0f;
        private bool m_IsEditing = false;

        private ModifyScatterPointCloudUtility m_ScatterModUtility;

        [MenuItem("Tools/Modify Scattering Data")]
        static void Init()
        {
            EditorWindow window = CreateInstance<ModifyScatterPointCloudTool>();
            window.Show();
        }


        private void OnEnable()
        {
            AssemblyReloadEvents.beforeAssemblyReload += EndWithoutSaving;
        }

        private void OnDisable()
        {
            m_SerializedObject = null;
            EndEditing(false);
        }

        void StartEditing()
        {
            SetScatterEnabled(false);
            m_IsEditing = true;

            if (m_ScatterModUtility == null)
            {
                m_ScatterModUtility = new ModifyScatterPointCloudUtility();
                m_ScatterModUtility.SetVisibleRadius(m_VisibleRadius);
                m_ScatterModUtility.SetOmissionRadius(m_OmitArea);
            }
            
            m_ScatterModUtility.BeginEdit(m_Targets, 100.0f);
            
            AssemblyReloadEvents.beforeAssemblyReload -= EndWithoutSaving;
        }

        void EndWithoutSaving()
        {
            EndEditing(false);
        }

        void EndEditing(bool save)
        {
            SetScatterEnabled(true);
            m_IsEditing = false;
            if (m_ScatterModUtility != null)
            {
                m_ScatterModUtility.EndEdit(save);
                m_ScatterModUtility = null;
            }
            
            
        }

        void SetScatterEnabled(bool enable)
        {
            var world = World.DefaultGameObjectInjectionWorld;
            if (world == null) return;
            var pcSystem = world.GetExistingSystemManaged<ScatterPointCloudSystem>();
            if (pcSystem != null)
            {
                var cData = world.EntityManager.GetComponentDataRW<PointCloudSystemConfigData>(pcSystem.SystemHandle);
                cData.ValueRW.DisableScatter = !enable;
            }
        }

        void OnGUI()
        {
            if (m_SerializedObject == null)
            {
                m_SerializedObject = new SerializedObject(this);
            }

            EditorGUILayout.BeginVertical();
            DrawBaseSettings();

            m_ScatterModUtility?.SetVisibleRadius(m_VisibleRadius);

            if (m_IsEditing)
            {
                if (GUILayout.Button("Close & Save Changes"))
                {
                    EndEditing(true);
                }
                if (GUILayout.Button("Close & Discard Changes"))
                {
                    EndEditing(false);
                }
                
            }
            else
            {
                if (GUILayout.Button("Open For Edit"))
                {
                    StartEditing();
                }
                if(GUILayout.Button("Clear Overrides"))
                {
                    if (m_Targets != null)
                    {
                        foreach (var t in m_Targets)
                        {
                            t.pointCloudAsset.ClearOverrides();
                            t.pointCloudAsset.Serialize();
                        }
                    }
                }
            }


            EditorGUILayout.EndVertical();
        }

        void DrawBaseSettings()
        {
            m_VisibleRadius = EditorGUILayout.FloatField("Visible Radius", m_VisibleRadius);
            m_OmitArea = EditorGUILayout.FloatField("Modified Point Omission Area", m_OmitArea);
            var assetsList = m_SerializedObject.FindProperty("m_Targets");
            if (assetsList != null)
            {
                EditorGUILayout.PropertyField(assetsList, true);
            }

            m_SerializedObject.ApplyModifiedProperties();
        }
    }
}