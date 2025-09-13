using System.Collections;
using System.Collections.Generic;
using System.IO;
using Unity.Mathematics;
using UnityEditor;
using UnityEditorInternal;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;


namespace TimeGhost
{
    public class ScatterInstanceImpostorEditor : EditorWindow
    {
        [SerializeField] private PointCloudFromHoudiniAsset[] m_Targets;
        private SerializedObject m_SerializedObject;
        private SerializedProperty m_SerializedProperty;

        private uint m_FrameCount = 8;
        private uint m_PerFrameResolution = 256;
        private bool m_UseHemiOcta = true;
        private bool m_GenerateSpecSmoothness = false;
        private bool m_BakeIntoLODGroup = true;
        private float m_CameraDistanceFromTarget = 100.0f;
        private float m_AlphaBlurRadius = 0.05f;
        private float m_BoundsExtraMargin = 0.0f;
        private bool m_UseQuad = true;
        private float3 m_minBoundsToGenerateImpostor = new float3(0.3f, 0.3f, 0.3f);
        
        private OctaImpostorGenerator m_Generator = null;

        private OctaImpostorGenerator.GenerateImpostorSettings m_GenerateImpostors;
        private LODGroup m_sourceLodGroup;

        private const string c_FileNamePostFix = "_impGen";
        private const string c_FolderName = "GeneratedInstanceImpostors";
        
        
        private const string c_HealthyTextureName = "_Base_Color_Map";
        private const string c_DryTextureName = "_Base_Color_Map_Dry";
        
        private const string c_HealthyColoreName = "_HealthyColor";
        private const string c_DryColorName = "_DryColor";

        [MenuItem("Tools/Impostor/PointCloud Impostor Generator")]
        static void Init()
        {
            EditorWindow window = CreateInstance<ScatterInstanceImpostorEditor>();
            window.Show();
        }


        private void OnEnable()
        {
            if (m_Generator == null)
            {
                m_Generator = new OctaImpostorGenerator();
                m_Generator.Init();
            }

            m_SerializedObject = new SerializedObject(this);
            m_SerializedProperty = m_SerializedObject.FindProperty("m_Targets");
        }

        private void OnDisable()
        {
            if (m_Generator != null)
            {
                m_Generator.Deinit();
                m_Generator = null;
            }
        }

        void OnGUI()
        {
            EditorGUILayout.BeginVertical();
            DrawBaseSettings();
            if (GUILayout.Button("Generate"))
            {
                Generate();
            }

            EditorGUILayout.EndVertical();
        }

        void DrawBaseSettings()
        {
            EditorGUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            EditorGUILayout.LabelField("Frame Count");
            m_FrameCount = math.max(4u, (uint)EditorGUILayout.IntField((int)m_FrameCount, EditorStyles.numberField));
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            EditorGUILayout.LabelField("Frame Resolution");
            m_PerFrameResolution = math.max(4u, (uint)EditorGUILayout.IntField((int)m_PerFrameResolution, EditorStyles.numberField));
            EditorGUILayout.EndHorizontal();

            m_UseHemiOcta = EditorGUILayout.Toggle("HemiOctahedron", m_UseHemiOcta);
            m_UseQuad = EditorGUILayout.Toggle("Use Quad", m_UseQuad);
            m_GenerateSpecSmoothness = EditorGUILayout.Toggle("Generate Specular Smoothness", m_GenerateSpecSmoothness);
            m_BoundsExtraMargin = EditorGUILayout.FloatField("Extra Bounds Margin", m_BoundsExtraMargin);
            m_minBoundsToGenerateImpostor = EditorGUILayout.Vector3Field("Min Bounds", m_minBoundsToGenerateImpostor);
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("DEBUG");

            EditorGUILayout.PropertyField(m_SerializedProperty, true);
            m_SerializedObject.ApplyModifiedProperties();
        }

        Texture2D CopyLastMip(Texture2D source)
        {
            int w = source.width;
            int h = source.height;
            
            RenderTexture tempRT = RenderTexture.GetTemporary(
                w,
                h,
                0,
                RenderTextureFormat.ARGB32,
                RenderTextureReadWrite.Linear);

            Graphics.Blit(source, tempRT);
            
            RenderTexture prevRT = RenderTexture.active;
            RenderTexture.active = tempRT;
            
            Texture2D copyTex = new Texture2D(w, h);
            copyTex.ReadPixels(new Rect(0, 0, w, h), 0, 0);
            copyTex.Apply();
            
            RenderTexture.active = prevRT;
            RenderTexture.ReleaseTemporary(tempRT);
            return copyTex;
        }
        
        bool CalculateHealthyAndDryColor(LODGroup lodGroup, out float3 healthyColor, out float3 dryColor)
        {
            healthyColor = 1.0f;
            dryColor = 1.0f;
            
            var lods = lodGroup.GetLODs();
            if (lods == null || lods.Length == 0) return false;
            
            var renderers = lods[0].renderers;
            if (renderers == null || renderers.Length == 0) return false;

            var mat = renderers[0].sharedMaterial;
            if (mat == null) return false;

            var healthyTex = mat.GetTexture(c_HealthyTextureName) as Texture2D;
            var dryTex = mat.GetTexture(c_DryTextureName) as Texture2D;
            
            if(healthyTex == null || dryTex == null) return false;

            if (!healthyTex.isReadable)
            { 
                healthyTex = CopyLastMip(healthyTex);
            }
            
            if (!dryTex.isReadable)
            { 
                dryTex = CopyLastMip(dryTex);
            }
            
            var healthyPixels = healthyTex.GetPixels(healthyTex.mipmapCount - 1);
            var dryPixels = dryTex.GetPixels(dryTex.mipmapCount - 1);
            
            float3 sum = 0;
            float weight = 0;
            for (int i = 0; i < healthyPixels.Length; ++i)
            {
                sum += new float3(healthyPixels[i].r, healthyPixels[i].g, healthyPixels[i].b) * healthyPixels[i].a;
                weight += healthyPixels[i].a;
            }
            healthyColor = sum / weight;
            
            sum = 0;
            weight = 0;
            for (int i = 0; i < dryPixels.Length; ++i)
            {
                sum += new float3(dryPixels[i].r, dryPixels[i].g, dryPixels[i].b) * dryPixels[i].a;
                weight += dryPixels[i].a;
            }

            dryColor = sum / weight;
            
            return true;
        }

        void Generate()
        {
            if (m_Targets.Length == 0) return;

            foreach (var target in m_Targets)
            {
                if (target == null) continue;

                var pcData = target.GetPointCloudData();

                if (pcData == null) return;

                for (int i = 0; i < pcData.Length; ++i)
                {
                    var prefab = GetOriginalPrefab(pcData[i].prefab);
                    if (prefab == null) continue;
                    
                    var impostorSources = GatherImpostorSources(prefab);

                    if (impostorSources.Length == 0)
                    {
                        continue;
                    }

                    var meshBounds = impostorSources[0].mesh.bounds;
                    if (meshBounds.size.x < m_minBoundsToGenerateImpostor.x ||
                        meshBounds.size.y < m_minBoundsToGenerateImpostor.y ||
                        meshBounds.size.z < m_minBoundsToGenerateImpostor.z)
                    {
                        Debug.Log($"Skipping Impostor generation for {prefab.name} because bounds very not big enough {meshBounds.size}");
                        continue;
                    }
                    
                    string assetPath = PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(prefab);
                    var directory = Path.Combine(Path.GetDirectoryName(assetPath), c_FolderName);
                    var filename = Path.GetFileNameWithoutExtension(assetPath) + c_FileNamePostFix;
                    
                    var renderer = new DefaultImpostorRenderer();
                    renderer.SetImpostorSources(impostorSources);
                    
                    OctaImpostorGenerator.GenerateImpostorSettings genSettings = default;
                    genSettings.frameCount = new uint2(m_FrameCount, m_FrameCount);
                    genSettings.frameResolution = new uint2(m_PerFrameResolution, m_PerFrameResolution);
                    genSettings.produceSpecularSmoothness = m_GenerateSpecSmoothness;
                    genSettings.useHemiOcta = m_UseHemiOcta;
                    genSettings.writeTexturesAsPNG = false;
                    genSettings.cameraDistance = m_CameraDistanceFromTarget;
                    genSettings.alphaBlurRatio = m_AlphaBlurRadius;
                    genSettings.renderer = renderer;
                    genSettings.overrideShader = Resources.Load<Shader>("ImpostorRuntime/OctahedralImpostorFoliagePointCloudSingleFrame");
                    genSettings.applyBoundsOffsetToMesh = true;
                    genSettings.generatorListener = new DefaultGeneratorUI();
                    genSettings.directoryPath = directory;
                    genSettings.impostorName = filename;
                    genSettings.meshType = m_UseQuad ? OctaImpostorGenerator.ImpostorMeshType.TightenedQuad : OctaImpostorGenerator.ImpostorMeshType.Octa;

                    float3 boundsMargin = new float3(m_BoundsExtraMargin, m_BoundsExtraMargin, m_BoundsExtraMargin);
                    genSettings.renderer.SetBoundsExtraMargin(boundsMargin);
                    bool addToLODGroup = prefab.TryGetComponent(out LODGroup lodGroup);
                    m_sourceLodGroup = lodGroup;
                    if (!addToLODGroup) continue;
                    
                    genSettings.position = float3.zero;
                    genSettings.rotation = quaternion.identity;
                    genSettings.scale = new float3(1.0f, 1.0f, 1.0f);
                    genSettings.prefabToClone = prefab;
                    

                    genSettings.customPostProcessAssetsCB = PostProcessImpostor;

                    m_GenerateImpostors = genSettings;
                    
                    var outputPath = m_Generator.GenerateImpostor(genSettings);
                    if (outputPath == null) continue;
                    
                    target.m_Prefabs[i] = AssetDatabase.LoadAssetAtPath<GameObject>(outputPath);
                }
                
                target.ApplyPrefabArrayEntriesToPCData();
            }
        }

        GameObject GetOriginalPrefab(GameObject go)
        {
            if (go == null) return null;
            var path = AssetDatabase.GetAssetPath(go);
            var directory = Path.GetDirectoryName(path);
            var fileName = Path.GetFileNameWithoutExtension(path);

            if (fileName.EndsWith(c_FileNamePostFix) && directory.Contains(c_FolderName))
            {
                var originalFileName = fileName.Substring(0, fileName.Length - c_FileNamePostFix.Length);
                var originalPath = directory.Substring(0, directory.Length - c_FolderName.Length);
                originalPath = Path.Combine(originalPath, originalFileName + Path.GetExtension(path));
                return AssetDatabase.LoadAssetAtPath<GameObject>(originalPath);
            }

            return go;
        }
        
        void PostProcessImpostor(GameObject go, Material mat, Mesh mesh, IImpostorContext.ImpostorTexture[] textures)
        {
            mat.SetMaterialType(MaterialId.LitTranslucent);

            if (CalculateHealthyAndDryColor(m_sourceLodGroup, out var healthyColor, out float3 dryColor))
            {
                mat.SetVector(c_HealthyColoreName, new Vector4(healthyColor.x, healthyColor.y, healthyColor.z));
                mat.SetVector(c_DryColorName, new Vector4(dryColor.x, dryColor.y, dryColor.z));
            }
            
            /*
            m_GenerateImpostors.prefabToClone.TryGetComponent(out LODGroup lodGroup);

            var bounds = m_GenerateImpostors.Renderer.GetBounds();
            float nearPlane;
            float farPlane;
            float fovX;
            float fovY;
            float camDistanceFromBoundsCenter;
            ImpostorGeneratorUtils.CalculateCameraParametersAroundBounds(bounds, m_GenerateImpostors.cameraDistance, out nearPlane, out farPlane, out fovX, out fovY, out camDistanceFromBoundsCenter);

            OctaImpostorGenerator.ImpostorSettings settings = new OctaImpostorGenerator.ImpostorSettings()
            {
                frameResolution = m_GenerateImpostors.frameResolution,
                frameCount = m_GenerateImpostors.frameCount,
                cameraExtraDistance = m_GenerateImpostors.cameraDistance,
                hemiOcta = m_GenerateImpostors.useHemiOcta,
                frameDilateLengthInTexels = 50,
                alphaSearchRadius = m_GenerateImpostors.alphaBlurRatio,
                applyBoundsOffsetToMesh = m_GenerateImpostors.applyBoundsOffsetToMesh,
                cameraSettings = new IImpostorContext.CameraSettings()
                {
                    farPlane = farPlane,
                    nearPlane = nearPlane,
                    fovY = fovY,
                    fovX = fovX
                }
            };

            IImpostorContext.ImpostorTexture[] impostorTextures = m_Generator.CreateImpostorTextures(settings, m_GenerateImpostors.Renderer,
                m_GenerateImpostors.generatorListener, m_GenerateImpostors.produceSpecularSmoothness);

            Texture2D albedoAlpha;
            foreach (var impostorTexture in impostorTextures)
            {
                if (impostorTexture.type == IImpostorContext.ImpostorTextureType.AlbedoAlpha)
                {
                    albedoAlpha = impostorTexture.texture;
                    break;
                }
            }*/
            
        }

        DefaultImpostorRenderer.ImpostorSource[] GatherImpostorSources(GameObject root)
        {
            MeshRenderer[] meshRenderers = root.GetComponentsInChildren<MeshRenderer>(false);

            HashSet<Renderer> renderersToOmit = new HashSet<Renderer>();

            //gather LODGRoups and only allow lod0
            LODGroup[] lodGroups = root.GetComponentsInChildren<LODGroup>(false);
            foreach (var lodGroup in lodGroups)
            {
                var lods = lodGroup.GetLODs();
                for (int i = 1; i < lods.Length; ++i)
                {
                    for (int k = 1; k < lods[i].renderers.Length; ++k)
                    {
                        renderersToOmit.Add(lods[i].renderers[k]);
                    }
                }
            }

            Matrix4x4 rootTransform = root.transform.worldToLocalMatrix;
            List<DefaultImpostorRenderer.ImpostorSource> impostorSourcesList = new List<DefaultImpostorRenderer.ImpostorSource>();
            foreach (var mr in meshRenderers)
            {
                if (renderersToOmit.Contains(mr)) continue;
                Material mat = mr.sharedMaterial;
                Mesh mesh = null;
                if (mr.TryGetComponent(out MeshFilter mf))
                {
                    mesh = mf.sharedMesh;
                }

                if (mesh != null && mat != null)
                {
                    impostorSourcesList.Add(new DefaultImpostorRenderer.ImpostorSource()
                    {
                        mesh = mesh,
                        mat = mat,
                        transform = math.mul(rootTransform, mr.transform.localToWorldMatrix)
                    });
                }
            }

            SkinnedMeshRenderer[] skinnedMeshRenderers = root.GetComponentsInChildren<SkinnedMeshRenderer>(false);
            foreach (var smr in skinnedMeshRenderers)
            {
                if (renderersToOmit.Contains(smr)) continue;
                if (smr.sharedMaterial == null || smr.sharedMesh == null) continue;
                Material mat = smr.sharedMaterial;
                Mesh mesh = Instantiate(smr.sharedMesh);

                smr.BakeMesh(mesh);

                impostorSourcesList.Add(new DefaultImpostorRenderer.ImpostorSource()
                {
                    mesh = mesh,
                    mat = mat,
                    transform = math.mul(rootTransform, smr.transform.localToWorldMatrix),
                    matPropBlock = null
                });
            }

            return impostorSourcesList.ToArray();
        }
    }
}