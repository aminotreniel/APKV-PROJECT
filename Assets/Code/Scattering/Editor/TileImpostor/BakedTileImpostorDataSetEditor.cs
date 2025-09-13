using System;
using System.Collections.Generic;
using Unity.Mathematics;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace TimeGhost
{
    [CustomEditor(typeof(BakedTileImpostorDataSet))]
    public class BakedTileImpostorDataSetEditor : Editor
    {
        private bool toggleGenerated = false;
        private bool toggleExtra = false;
        private SerializedObject m_SerializedEditorObject;
        [SerializeField]
        private ScatterPointCloudAuthoring[] m_ScatterObjects;
        private void OnEnable()
        {

        }

        public override void OnInspectorGUI()
        {
            var asset = (BakedTileImpostorDataSet)target;
            if (asset == null)
                return;

            if (asset.ImpostorMeshDefinitions == null || asset.ImpostorMeshDefinitions.Length == 0)
            {
                asset.ImpostorMeshDefinitions = new []
                {
                    new BakedTileImpostorDataSet.ImpostorMeshDefinition()
                    {
                        CardWidth = asset.CardWidth_Deprecated,
                        VerticesPerMeter = asset.VerticesPerMeter_Deprecated,
                        UseCards = asset.ImpostorType == BakedTileImpostorDataSet.TileImpostorType.TileCards
                    }
                };
                serializedObject.Update();
            }
            
            if (m_SerializedEditorObject == null || m_SerializedEditorObject.targetObject != this)
            {
                m_SerializedEditorObject = new SerializedObject(this);
            }
            
            var assets = serializedObject.FindProperty("Sources");
            EditorGUILayout.PropertyField(assets, new GUIContent("Bake Sources"));

            EditorGUILayout.PropertyField(serializedObject.FindProperty("ImpostorType"), new GUIContent("Impostor Type"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("ImpostorMeshDefinitions"), new GUIContent("Impostor Mesh Definitions"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("FrameResolution"), new GUIContent("Tile Texture Resolution"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("FrameCount"), new GUIContent("Frame Count (Only Octa Impostor)"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("DownSampleCount"), new GUIContent("Downsampling Count"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("TileSize"), new GUIContent("Size of TileImpostor (meters)"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("DiscardTileImpostorRatio"), new GUIContent("Impostor Tile Discard Ratio "));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("ExtraBoundsMargin"), new GUIContent("Extra Margin added to tile bounds"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("HeightBias"), new GUIContent("Impostor Height Bias"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("OverrideDiffusionProfile"), new GUIContent("Diffusion Profile Override"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("UseAreaLimit"), new GUIContent("Use Area Limit"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("AreaLimit"), new GUIContent("Area Limit"));
            
            
            serializedObject.ApplyModifiedProperties();
            if (GUILayout.Button("Generate Impostors"))
            {
                Generate(asset);
                serializedObject.Update();
            }

            if (asset != null)
            {
                var path = BakedTileImpostorDataSetGenerator.GetDirectoryPath(asset);
                if (path != null)
                {
                    var message = $"Regenerating impostors will completely delete contents of {path}";
                    EditorGUILayout.HelpBox(message, MessageType.Warning);
                }
            }

            
            toggleExtra = EditorGUILayout.BeginToggleGroup("More", toggleExtra);
            if (toggleExtra)
            {
                var scatterObjects = m_SerializedEditorObject.FindProperty("m_ScatterObjects");
                EditorGUILayout.PropertyField(scatterObjects, new GUIContent("Add Scatter Objects To Bake Sources"));
                m_SerializedEditorObject.ApplyModifiedProperties();
                GUI.enabled = true;
                if (GUILayout.Button("Add To Impostor Sources"))
                {
                    if (m_ScatterObjects != null)
                    {
                        List<BakedTileImpostorDataSet.ImpostorBakeSource> newSources = new List<BakedTileImpostorDataSet.ImpostorBakeSource>();
                    
                        foreach (var scatterObject in m_ScatterObjects)
                        {
                            if (scatterObject != null && scatterObject.pointCloudAsset != null)
                            {
                                float4x4 transform = scatterObject.transform.localToWorldMatrix;
                                newSources.Add(new BakedTileImpostorDataSet.ImpostorBakeSource()
                                {
                                    Transform = transform,
                                    Asset = scatterObject.pointCloudAsset
                                });
                            }
                        
                        }

                        if (asset.Sources != null)
                        {
                            newSources.AddRange(asset.Sources);
                        }

                        asset.Sources = newSources.ToArray();
                        EditorUtility.SetDirty(asset);
                        AssetDatabase.SaveAssetIfDirty(asset);
                        serializedObject.Update();
                    }

                    m_ScatterObjects = null;
                    m_SerializedEditorObject.Update();

                }
            }
            EditorGUILayout.EndToggleGroup();

            toggleGenerated = EditorGUILayout.BeginToggleGroup("Show Generated Entries", toggleGenerated);
            if (toggleGenerated)
            {
                GUI.enabled = false;
                var partitionInfo = serializedObject.FindProperty("PartitioningInfo");
                EditorGUILayout.PropertyField(partitionInfo, new GUIContent("Partitioning"));
                var generatedTileImpostors= serializedObject.FindProperty("GeneratedImposters");
                EditorGUILayout.PropertyField(generatedTileImpostors, new GUIContent("Generated Imposters"));
                GUI.enabled = true;
            }
            EditorGUILayout.EndToggleGroup();

        }


        void Generate(BakedTileImpostorDataSet dataSet)
        {
            try
            {
                
                if (dataSet.Sources.Length > 0)
                {
                    
                    BakedTileImpostorDataSetGenerator generator = new BakedTileImpostorDataSetGenerator();
                    dataSet.FrameResolution = math.ceilpow2(dataSet.FrameResolution);
                    generator.Generate(dataSet);
                }

                for (int i = 0; i < dataSet.Sources.Length; ++i)
                {
                    var asset = dataSet.Sources[i].Asset;
                    if (asset.m_IgnoreMaxScatterDistance)
                    {
                        asset.m_IgnoreMaxScatterDistance = false; 
                        EditorUtility.SetDirty(asset);
                        AssetDatabase.SaveAssetIfDirty(asset);
                    }
                   
                }
                
                
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            } 
            
        }
    }
}