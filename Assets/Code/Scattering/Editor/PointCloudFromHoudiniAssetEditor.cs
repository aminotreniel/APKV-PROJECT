using UnityEditor;
using UnityEditor.AssetImporters;
using UnityEngine;
using UnityEngine.UIElements;

namespace TimeGhost
{
    [CustomEditor(typeof(PointCloudFromHoudiniAsset))]
    public class PointCloudFromHoudiniAssetEditor : Editor
    {
        public bool showAdvanced = false;
        public bool drawPrefabs = false;

        public override void OnInspectorGUI()
        {
            var asset = (PointCloudFromHoudiniAsset)target;
            if (asset == null)
                return;


            EditorGUILayout.LabelField($"Total Number Of Instances: {asset.TotalNumberOfInstances}");
            
            {
                var pointCaches = serializedObject.FindProperty("m_PointCaches");
                EditorGUILayout.PropertyField(pointCaches, new GUIContent("Point Caches"));
                if (pointCaches.arraySize > 0)
                {
                    if (GUILayout.Button("ExtractAndBake"))
                    {
                        ExtractPointCloudData();
                        BakeData();
                    }
                }
            }

            /*if (GUILayout.Button("Deserialize"))
            {
                asset.Deserialize();
            }

            if (GUILayout.Button("ExtractAndBakeAllInProject"))
            {
                var paths = AssetDatabase.GetAllAssetPaths();
                foreach (var path in paths)
                {
                    if (AssetDatabase.GetMainAssetTypeAtPath(path) == typeof(PointCloudFromHoudiniAsset))
                    {
                        var houdiniAsset = AssetDatabase.LoadAssetAtPath<PointCloudFromHoudiniAsset>(path);

                        Debug.Log($"Rebaking {path}");
                        ExtractPointCloudData(houdiniAsset, true);
                    }
                }
            }*/
            
            showAdvanced = EditorGUILayout.Foldout(showAdvanced, "Advanced");
            if (showAdvanced)
            {
                EditorGUILayout.BeginHorizontal();
                if (GUILayout.Button("ExtractPointCloudData"))
                {
                    ExtractPointCloudData();
                }

                if (GUILayout.Button("Bake"))
                {
                    BakeData();
                }

                EditorGUILayout.EndHorizontal();

                drawPrefabs = EditorGUILayout.Toggle("Show Prefab List: ", drawPrefabs);
                if (drawPrefabs)
                {
                    if (GUILayout.Button("Apply prefab overrides"))
                    {
                        asset.ApplyPrefabArrayEntriesToPCData();
                        EditorUtility.SetDirty(asset);
                        Undo.ClearUndo(asset);
                        AssetDatabase.SaveAssetIfDirty(asset);
                    }

                    var prefabs = serializedObject.FindProperty("m_Prefabs");
                    EditorGUILayout.PropertyField(prefabs);
                }
                
                EditorGUILayout.PropertyField(serializedObject.FindProperty("m_IgnoreMaxScatterDistance"), new GUIContent("Ignore Max Scatter Distance"));
            }
        }


        void ExtractPointCloudData()
        {
            serializedObject.ApplyModifiedProperties();
            var asset = (PointCloudFromHoudiniAsset)target;
            ExtractPointCloudData(asset);
            serializedObject.Update();
        }

        void ExtractPointCloudData(PointCloudFromHoudiniAsset asset, bool tryToMaintainPrefabOverride = false)
        {
            var pcCount = asset.m_PointCaches.Length;
            PointCloudFromHoudiniAsset.PointCloudData[] newData = new PointCloudFromHoudiniAsset.PointCloudData[pcCount];
            for (int i = 0; i < pcCount; ++i)
            {
                var prefabsList = asset.m_Prefabs;
                GameObject previousPrefab = null;
                if (tryToMaintainPrefabOverride)
                {
                    if (prefabsList != null && prefabsList.Length > i)
                    {
                        previousPrefab = prefabsList[i];
                    }
                }
                
                ExtractData(asset.m_PointCaches[i], ref newData[i], previousPrefab);
            }

            asset.SetUnmodifiedPointCloudData(newData);
            asset.Serialize();
            EditorUtility.SetDirty(asset);
            Undo.ClearUndo(asset);
            AssetDatabase.SaveAssetIfDirty(asset);
        }

        void BakeData()
        {
            serializedObject.FindProperty("m_ScatteredCount").intValue += 1;
            serializedObject.ApplyModifiedProperties();
            var asset = (PointCloudFromHoudiniAsset)target;
            EditorUtility.SetDirty(asset);
            Undo.ClearUndo(asset);
            AssetDatabase.SaveAssetIfDirty(asset);
        }

        void ExtractData(Object pc, ref PointCloudFromHoudiniAsset.PointCloudData data, GameObject prefabOverride = null)
        {
            int pointsLoaded = PointCloudFromHoudini.LoadPointCloudData(pc, ref data.positions, ref data.rotations,
                ref data.scales, ref data.age, ref data.health, ref data.color, ref data.partIndices);

            if (pointsLoaded == 0) return;
            string guid = null;
            if (prefabOverride != null)
            {
                guid = AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(prefabOverride));
            }

            if (guid == null)
            {
                guid = PointCloudFromHoudini.GetOrCreatePrefabForPointCache(pc);
            }

            if (string.IsNullOrEmpty(guid)) return;

            if (data.positions.Length != pointsLoaded || data.rotations.Length != pointsLoaded ||
                data.scales.Length != pointsLoaded)
            {
                Debug.LogError($"Couldn't load pointcloud {pc.name} because attribute counts don't match!");
                return;
            }

            data.prefab = AssetDatabase.LoadAssetAtPath<GameObject>(AssetDatabase.GUIDToAssetPath(guid));
            ;
        }
    }
}