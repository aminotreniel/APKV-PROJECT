using System;
using System.Collections.Generic;
using System.IO;
using Unity.Mathematics;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace TimeGhost
{
    public class ScatteredPrefabBatchTool : EditorWindow
    {
        public enum LODRemovalMode
        {
            Highest,
            Lowest,
            Middle
        }
        public List<GameObject> m_PrefabList = new List<GameObject>();
        private SerializedObject m_SerializedObject;

        private string m_FileSearchPath = "Assets/Art/Vegetation";
        private string m_FileSearchFilter = "_pc_prefab";
#if HAS_ENVIRONMENT
        private EnvironmentPointCloudFromHoudiniAsset pointCloudAsset;
#endif
        private PointCloudFromHoudiniAsset pointCloudAssetNoEnv;

        private string m_PrefabGenerationPostfix = "_pc_prefab";

        private ScatteringPrefabInteractiveParameters m_ScatteringPrefabComponentPrototype;

        private bool m_FoldoutPrefabGeneration;
        private bool m_FoldoutModifyScatterPrefabInteractive;
        private bool m_FoldoutModifyScatterPrefab;

        private bool m_FoldoutListFindWithFilter;
        private bool m_FoldoutListFindFromPC;
        private int m_MinimumLODsToRemain = 1;
        private int m_NumberOfLODsToRemove = 4;
        private LODRemovalMode m_LodRemovalMode;
        private int m_lowestLODLevelsToNotCastShadows = 0;
        //private float m_removedLODRangeMerge = 0.5f;
        
        private Vector2 m_ScrollPosition;

        // Add menu item
        [MenuItem("Tools/Batch Author Scattered Prefabs")]
        static void Init()
        {
            EditorWindow window = CreateInstance<ScatteredPrefabBatchTool>();
            window.Show();
        }

        private void OnEnable()
        {
            m_SerializedObject = new SerializedObject(this);
        }

        void OnGUI()
        {
            DrawPrefabGenerationSection();
            EditorGUILayout.Separator();
            DrawModifyScatteringPrefabInteractive();
            EditorGUILayout.Separator();
            DrawModifyScatteringPrefab();
            EditorGUILayout.Separator();
            DrawListModificationSection();
            EditorGUILayout.Separator();
            
            m_ScrollPosition = EditorGUILayout.BeginScrollView(m_ScrollPosition);
            
            SerializedProperty prefabs = m_SerializedObject.FindProperty("m_PrefabList");
            EditorGUILayout.PropertyField(prefabs, true);
            EditorGUILayout.EndScrollView();
            
            m_SerializedObject.ApplyModifiedProperties();
        }

        void DrawPrefabGenerationSection()
        {
            m_FoldoutPrefabGeneration =
                EditorGUILayout.BeginFoldoutHeaderGroup(m_FoldoutPrefabGeneration, "Generate Prefabs");

            if (m_FoldoutPrefabGeneration)
            {
                //GUILayout.Label("Generated Prefab Postfix");
                //m_PrefabGenerationPostfix = GUILayout.TextField(m_PrefabGenerationPostfix);
                
                if (GUILayout.Button("Generate Prefabs"))
                {
                    foreach (var prefab in m_PrefabList)
                    {
                        string path = AssetDatabase.GetAssetPath(prefab);
                        if (string.IsNullOrEmpty(path)) continue;
                        var newPath = Path.Combine(Path.GetDirectoryName(path), Path.GetFileNameWithoutExtension(path) + m_PrefabGenerationPostfix + ".prefab");
                        if (AssetDatabase.AssetPathExists(newPath)) continue;
                        var asset = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                        var newAsset = Instantiate(asset);
                        
                        if (!newAsset.TryGetComponent(out LODGroup lodGroup))
                        {
                            if (!newAsset.TryGetComponent(out MeshFilter meshFilter))
                            {
                                //try to find a valid gameobject from children (valid in this case is something with a mesh and meshrenderer
                                GameObject goToCopy = null;
                                for (int i = 0; i < newAsset.transform.childCount; ++i)
                                {
                                    var child = newAsset.transform.GetChild(i);
                                    if (child.TryGetComponent(out MeshFilter mfChild) && child.TryGetComponent(out MeshRenderer mrChild))
                                    {
                                        goToCopy = child.gameObject;
                                        break;
                                    }
                                }

                                if (goToCopy)
                                {
                                    goToCopy.transform.SetParent(null);
                                    newAsset = goToCopy;
                                }
                                
                            }
                        }
                        
                        PrefabUtility.SaveAsPrefabAsset(newAsset, newPath);

                    }
                }
            }
            
            EditorGUILayout.EndFoldoutHeaderGroup();
        }

        void DrawModifyScatteringPrefabInteractive()
        {
            m_FoldoutModifyScatterPrefabInteractive =
                EditorGUILayout.BeginFoldoutHeaderGroup(m_FoldoutModifyScatterPrefabInteractive, "Modify Scattering Prefab Interactive");
            if (m_FoldoutModifyScatterPrefabInteractive)
            {
                m_ScatteringPrefabComponentPrototype = (ScatteringPrefabInteractiveParameters)EditorGUILayout.ObjectField("Prototype",
                    m_ScatteringPrefabComponentPrototype, typeof(ScatteringPrefabInteractiveParameters));

                if (GUILayout.Button("Add/Set Scattering Prefab Component"))
                {
                    foreach (var obj in m_PrefabList)
                    {
                        GameObject go = obj;
                        ScatteringPrefabInteractiveParameters comp;
                        if (!go.TryGetComponent(out comp))
                        {
                            comp = go.AddComponent<ScatteringPrefabInteractiveParameters>();
                        }

                        if (m_ScatteringPrefabComponentPrototype != null)
                        {
                            comp.SetFrom(m_ScatteringPrefabComponentPrototype);
                        }
                    }

                    AssetDatabase.SaveAssets();
                }

                if (GUILayout.Button("Remove Scattering Prefab Component"))
                {
                    foreach (var prefab in m_PrefabList)
                    {
                        GameObject go = prefab;
                        if (go == null) continue;
                        DestroyImmediate(go.GetComponent<ScatteringPrefabInteractiveParameters>());
                        EditorUtility.SetDirty(prefab);
                    }

                    AssetDatabase.SaveAssets();
                }

            }
            
            EditorGUILayout.EndFoldoutHeaderGroup();
        }

        void DrawModifyScatteringPrefab()
        {
            m_FoldoutModifyScatterPrefab =
                EditorGUILayout.BeginFoldoutHeaderGroup(m_FoldoutModifyScatterPrefab, "Modify Scattering Prefab");
            if (m_FoldoutModifyScatterPrefab)
            {
                EditorGUILayout.BeginHorizontal();
                if (GUILayout.Button("Add Scattering Prefab Component"))
                {
                    foreach (var obj in m_PrefabList)
                    {
                        GameObject go = obj;
                        ScatteringPrefab comp;
                        if (!go.TryGetComponent(out comp))
                        {
                            go.AddComponent<ScatteringPrefab>();
                        }
                    }

                    AssetDatabase.SaveAssets();
                }

                if (GUILayout.Button("Remove Scattering Prefab Component"))
                {
                    foreach (var prefab in m_PrefabList)
                    {
                        GameObject go = prefab;
                        if (go == null) continue;
                        DestroyImmediate(go.GetComponent<ScatteringPrefab>());
                        EditorUtility.SetDirty(prefab);
                    }

                    AssetDatabase.SaveAssets();
                }
                EditorGUILayout.EndHorizontal();

                m_MinimumLODsToRemain = EditorGUILayout.IntField("Minimum LODs To Remain", m_MinimumLODsToRemain);
               // m_removedLODRangeMerge = EditorGUILayout.FloatField("How to merge removed lod transition", m_removedLODRangeMerge);
               m_NumberOfLODsToRemove = EditorGUILayout.IntField("Number Of LODs To Remove", m_NumberOfLODsToRemove);
               m_lowestLODLevelsToNotCastShadows = EditorGUILayout.IntField("Number Of Lowest LODs to not Cast Shadows", m_lowestLODLevelsToNotCastShadows);
               m_LodRemovalMode = (LODRemovalMode)EditorGUILayout.EnumPopup("LOD Removal Mode", m_LodRemovalMode);

                if (GUILayout.Button("Prune LODs"))
                {
                    PruneLODLevels();

                    AssetDatabase.SaveAssets();
                }

            }
            
            EditorGUILayout.EndFoldoutHeaderGroup();
        }

        void DrawListModificationSection()
        {
            m_FoldoutListFindWithFilter =
                EditorGUILayout.BeginFoldoutHeaderGroup(m_FoldoutListFindWithFilter, "Find");
            if (m_FoldoutListFindWithFilter)
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label("Search Path");
                m_FileSearchPath = GUILayout.TextField(m_FileSearchPath);
                GUILayout.EndHorizontal();

                GUILayout.BeginHorizontal();
                GUILayout.Label("Search Filter");
                m_FileSearchFilter = GUILayout.TextField(m_FileSearchFilter);
                GUILayout.EndHorizontal();

                if (GUILayout.Button("Search & Add to list"))
                {
                    string[] Guids = AssetDatabase.FindAssets(m_FileSearchFilter, new[] { m_FileSearchPath });
                    foreach (var guid in Guids)
                    {
                        var path = AssetDatabase.GUIDToAssetPath(guid);
                        GameObject obj = AssetDatabase.LoadAssetAtPath<GameObject>(path);

                        if (obj != null)
                        {
                            m_PrefabList.Add(obj);
                        }
                    }

                    m_SerializedObject.Update();
                }
            }
            EditorGUILayout.EndFoldoutHeaderGroup();
            
            m_FoldoutListFindFromPC =
                EditorGUILayout.BeginFoldoutHeaderGroup(m_FoldoutListFindFromPC, "Populate from PointCloud");
            if (m_FoldoutListFindFromPC)
            {
                pointCloudAssetNoEnv =
                    (PointCloudFromHoudiniAsset)EditorGUILayout.ObjectField("PointCloudAsset", pointCloudAssetNoEnv,
                        typeof(PointCloudFromHoudiniAsset));
#if HAS_ENVIRONMENT
                pointCloudAsset =
                    (EnvironmentPointCloudFromHoudiniAsset)EditorGUILayout.ObjectField("PointCloudAsset", pointCloudAsset,
                        typeof(EnvironmentPointCloudFromHoudiniAsset));
#endif
                
                if (
#if HAS_ENVIRONMENT
                    pointCloudAsset || 
#endif
                    pointCloudAssetNoEnv
                )
                {
                    if (GUILayout.Button("Add Point Cloud Prefabs To List"))
                    {
                        List<string> guidsToAdd = new List<string>();

#if HAS_ENVIRONMENT
                        if (pointCloudAsset)
                        {
                            foreach (Object obj in pointCloudAsset.pointCaches)
                            {
                                var guid = PointCloudFromHoudini.GetOrCreatePrefabForPointCache(obj);
                                if (guid != null)
                                {
                                    guidsToAdd.Add(guid);
                                }
                            }
                        }
#endif
                        
                        if (pointCloudAssetNoEnv)
                        {
                            foreach (Object obj in pointCloudAssetNoEnv.m_PointCaches)
                            {
                                var guid = PointCloudFromHoudini.GetOrCreatePrefabForPointCache(obj);
                                if (guid != null)
                                {
                                    guidsToAdd.Add(guid);
                                }
                            }
                        }

                        foreach (var p in guidsToAdd)
                        {
                            string prefabPath = AssetDatabase.GUIDToAssetPath(p);
                            GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
                            m_PrefabList.Add(prefab);
                        }

                        m_SerializedObject.Update();
                    }

                    
                }
            }
            
            
            EditorGUILayout.EndFoldoutHeaderGroup();
        }

        void PruneLODLevels()
        {

            List<LOD> lodsTemp = new List<LOD>(64);
            for(int prefabIndex = 0; prefabIndex < m_PrefabList.Count; ++prefabIndex)
            {
                GameObject asset = m_PrefabList[prefabIndex];
                var path = AssetDatabase.GetAssetPath(asset);
                var prefabGO = PrefabUtility.LoadPrefabContents(path);
                if (prefabGO.TryGetComponent(out LODGroup lodGroup))
                {
                    var lodCount = lodGroup.lodCount;
                    int lodsToRemove = lodCount - math.max(m_MinimumLODsToRemain, lodCount - m_NumberOfLODsToRemove);

                    bool removeAllLODs = false;
                    
                    if (lodsToRemove > 0)
                    {
                        lodsTemp.Clear();

                        removeAllLODs = m_MinimumLODsToRemain == 0 && lodGroup.lodCount - lodsToRemove == 0;

                        if (removeAllLODs)
                        {
                            var lod = lodGroup.GetLODs()[0];
                            var rend = lod.renderers[0];

                            if (rend.TryGetComponent(out MeshFilter mf))
                            {
                                var mfT = prefabGO.AddComponent<MeshFilter>();
                                mfT.sharedMesh = mf.sharedMesh;
                            }


                            if (rend is MeshRenderer)
                            {
                                var mr = prefabGO.AddComponent<MeshRenderer>();
                                mr.sharedMaterial = ((MeshRenderer)rend).sharedMaterial;
                            }

                            while (prefabGO.transform.childCount > 0)
                            {
                                DestroyImmediate(prefabGO.transform.GetChild(0).gameObject);
                            }

                            DestroyImmediate(lodGroup);

                        }
                        else
                        {
                            int lodCountAfterRemoval = lodCount - lodsToRemove;

                            var lods = lodGroup.GetLODs();
                            for (int i = 0; i < lods.Length; ++i)
                            {
                                lodsTemp.Add(lods[i]);
                            }

                            switch (m_LodRemovalMode)
                            {
                                case LODRemovalMode.Highest:
                                    for (int i = 0; i < lodsToRemove; ++i)
                                    {
                                        var lodToDelete = lodsTemp[0];
                                        ;
                                        DestroyImmediate(lodToDelete.renderers[0].gameObject);
                                        lodsTemp.RemoveAt(0);
                                    }

                                    break;
                                case LODRemovalMode.Lowest:
                                    for (int i = 0; i < lodsToRemove; ++i)
                                    {
                                        var lodToDelete = lodsTemp[^1];
                                        DestroyImmediate(lodToDelete.renderers[0].gameObject);
                                        lodsTemp.RemoveAt(lodsTemp.Count - 1);
                                    }

                                    break;
                                case LODRemovalMode.Middle:
                                    while (lodsTemp.Count > lodCountAfterRemoval)
                                    {
                                        var indexToDelete = (lodsTemp.Count / 2);
                                        var lodToDelete = lodsTemp[indexToDelete];
                                        DestroyImmediate(lodToDelete.renderers[0].gameObject);
                                        lodsTemp.RemoveAt(indexToDelete);
                                    }

                                    break;
                                default:
                                    throw new ArgumentOutOfRangeException();
                            }
                            lodGroup.SetLODs(lodsTemp.ToArray());
                        }

                    }

                    if (m_lowestLODLevelsToNotCastShadows > 0 && !removeAllLODs)
                    {
                        var toLOD = lodGroup.lodCount;
                        var fromLOD = Mathf.Max(0, lodGroup.lodCount - m_lowestLODLevelsToNotCastShadows);

                        for (int i = fromLOD; i < toLOD; ++i)
                        {
                            foreach (var rend in lodGroup.GetLODs()[i].renderers)
                            {
                                if (rend != null)
                                {
                                    rend.shadowCastingMode = ShadowCastingMode.Off;
                                }
                            }

                            
                        }
                        
                    }
                }

                PrefabUtility.SaveAsPrefabAsset(prefabGO, path);
                PrefabUtility.UnloadPrefabContents(prefabGO);
            }
            
            
        }
    }
}
