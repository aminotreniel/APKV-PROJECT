using System.Collections;
using System.Collections.Generic;
using System.IO;
using Unity.Mathematics;
using UnityEditor;
using UnityEditorInternal;
using UnityEngine;
using UnityEngine.Rendering;


namespace TimeGhost
{
    [InitializeOnLoad]
    public static class HookAssetLink
    {
        static HookAssetLink()
        {
            EditorGUI.hyperLinkClicked -= AssetLinkClicked;
            EditorGUI.hyperLinkClicked += AssetLinkClicked;
        }
        
        private static void AssetLinkClicked(EditorWindow w, HyperLinkClickedEventArgs a)
        {
            if (a.hyperLinkData.TryGetValue("imposter", out string val))
            {
                EditorUtility.FocusProjectWindow();

                var obj = AssetDatabase.LoadAssetAtPath<Object>(val);
 
                Selection.activeObject = obj;
                
            }
        }
    }

    public class DefaultGeneratorUI : IOctaImpostorGeneratorListener
    {
        public void OnBeginFrameRender(OctaImpostorGenerator.ImpostorSettings settings, uint2 frameBeingRendered)
        {
            EditorUtility.DisplayProgressBar("OctaImpostorGenerator", $"Render Impostor frame x:{frameBeingRendered.x}/{settings.frameCount.x}, y:{frameBeingRendered.y}/{settings.frameCount.y}", (float)(frameBeingRendered.x * settings.frameCount.y + frameBeingRendered.y)/(settings.frameCount.x * settings.frameCount.y));
        }
    }
    

    
    public class OctaImpostorGeneratorEditor : EditorWindow
    {
        public struct PropertyMapping
        {
            public string sourceName;
            public string destinationName;
        }
        [SerializeField]
        private GameObject[] m_Targets;
        private SerializedObject m_SerializedObject;
        private SerializedProperty m_SerializedProperty;
        
        private uint2 m_FrameCount = new uint2(8, 8);
        private uint2 m_PerFrameResolution = new uint2(256, 256);
        private bool m_UseHemiOcta = true;
        private bool m_GenerateSpecSmoothness = false;
        private bool m_WriteTexturesAsPNG = false;
        private bool m_BakeIntoLODGroup = true;
        private float m_CameraDistanceFromTarget = 100.0f;
        private float m_AlphaBlurRadius = 0.05f;
        private float m_BoundsExtraMargin = 0.0f;
        private Material m_CustomOutputMaterial = null;
        private string m_CustomOutputImpostorPropertyName = "_ImpostorTextureMisc";
        

        private OctaImpostorGenerator m_Generator = null;
        
        //visualization
        private bool m_ShowVisualization;
        private bool m_VisDrawImpostorLocations = true;
        private int m_VisEntryIndexToVisualize = 0;
        private PreviewRenderUtility m_PreviewRenderer;
        private Mesh m_VisFrameMarkerMesh = null;
        private Material m_VisFrameMarkerMaterial = null;
        private MaterialPropertyBlock m_VisMatPropertyBlock = null;

        [MenuItem("Tools/Impostor/Impostor Generator")]
        static void Init()
        {
            EditorWindow window = CreateInstance<OctaImpostorGeneratorEditor>();
            window.Show();
        }
        
        private void OnEnable()
        {
            m_PreviewRenderer = new PreviewRenderUtility();
            m_PreviewRenderer.camera.clearFlags = CameraClearFlags.SolidColor;
            m_PreviewRenderer.camera.backgroundColor = Color.black;
            m_PreviewRenderer.camera.nearClipPlane = 0.001f;
            m_PreviewRenderer.camera.farClipPlane = 50.0f;
            m_PreviewRenderer.camera.fieldOfView = 50.0f;
            m_PreviewRenderer.camera.transform.position = Vector3.zero;
            m_PreviewRenderer.camera.transform.LookAt(Vector3.forward, Vector3.up);

            for (int i = 1; i != m_PreviewRenderer.lights.Length; i++)
            {
                m_PreviewRenderer.lights[i].intensity = 0.0001f;
            }

            if (m_VisFrameMarkerMesh == null)
            {
                m_VisFrameMarkerMesh = Resources.GetBuiltinResource<Mesh>("Cube.fbx");
            }

            if (m_VisFrameMarkerMaterial == null)
            {
                var shader = Resources.Load<Shader>("FrameMarkerShader");
                m_VisFrameMarkerMaterial = CoreUtils.CreateEngineMaterial(shader);
            }

            if (m_VisMatPropertyBlock == null)
            {
                m_VisMatPropertyBlock = new MaterialPropertyBlock();
            }

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
            if (m_PreviewRenderer != null)
            {
                m_PreviewRenderer.Cleanup();
                m_PreviewRenderer = null;
            }
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
            DrawVisualization();
            EditorGUILayout.EndVertical();
        }

        void DrawBaseSettings()
        {

            EditorGUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            EditorGUILayout.LabelField("Frame Count");
            m_FrameCount.x = math.max(4u, (uint)EditorGUILayout.IntField((int)m_FrameCount.x, EditorStyles.numberField));
            m_FrameCount.y = math.max(4u, (uint)EditorGUILayout.IntField((int)m_FrameCount.y, EditorStyles.numberField));
            EditorGUILayout.EndHorizontal();
            
            EditorGUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            EditorGUILayout.LabelField("Frame Resolution");
            m_PerFrameResolution.x = math.max(4u, (uint)EditorGUILayout.IntField((int)m_PerFrameResolution.x, EditorStyles.numberField));
            m_PerFrameResolution.y = math.max(4u, (uint)EditorGUILayout.IntField((int)m_PerFrameResolution.y, EditorStyles.numberField));
            EditorGUILayout.EndHorizontal();
            
            m_UseHemiOcta = EditorGUILayout.Toggle("HemiOctahedron", m_UseHemiOcta);
            m_GenerateSpecSmoothness = EditorGUILayout.Toggle("Generate Specular Smoothness", m_GenerateSpecSmoothness);
            m_CameraDistanceFromTarget = EditorGUILayout.FloatField("Camera Offset", m_CameraDistanceFromTarget);
            m_CameraDistanceFromTarget = math.max(m_CameraDistanceFromTarget, 0.00001f);
            m_AlphaBlurRadius = Mathf.Clamp(EditorGUILayout.FloatField("Alpha Blur Radius", m_AlphaBlurRadius), 0.0f, 0.8f);
            m_BakeIntoLODGroup = EditorGUILayout.Toggle("Bake to LODGroup", m_BakeIntoLODGroup);
            m_BoundsExtraMargin = EditorGUILayout.FloatField("Extra Bounds Margin", m_BoundsExtraMargin);
            EditorGUILayout.Space();

            m_CustomOutputMaterial = EditorGUILayout.ObjectField( "Custom Output Generator Material", m_CustomOutputMaterial, typeof(Material), true) as Material;
            m_CustomOutputImpostorPropertyName = EditorGUILayout.TextField( "Custom Output Impostor Material Property Name", m_CustomOutputImpostorPropertyName);
            
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("DEBUG");
            m_WriteTexturesAsPNG = EditorGUILayout.Toggle("Write Textures as PNG", m_WriteTexturesAsPNG);
            
            EditorGUILayout.PropertyField(m_SerializedProperty, true);
            m_SerializedObject.ApplyModifiedProperties();
        }

        void Generate()
        {

            if (m_Targets.Length == 0) return;

            foreach (var target in m_Targets)
            {
                if (target == null) continue;
                
                var path = AssetDatabase.GetAssetPath(target);
                string directory = null;
                string filename = null;
                if (string.IsNullOrEmpty(path))
                {
                    if (PrefabUtility.IsAnyPrefabInstanceRoot(target))
                    {
                        string assetPath = PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(target);
                        directory = Path.GetDirectoryName(assetPath);
                        filename = (target.scene.IsValid() ?  $"scene_{target.scene.name}_" : "") + Path.GetFileNameWithoutExtension(assetPath);
                    }
                    else
                    {
                        if (target.scene.IsValid())
                        {
                            var scenePath = target.scene.path;
                            directory = Path.GetDirectoryName(scenePath);
                            filename = target.name;
                        }
                        
                    }
                    
                }
                else
                {
                    directory = Path.GetDirectoryName(path);
                    filename = Path.GetFileNameWithoutExtension(path);
                }

                if (directory == null || filename == null)
                {
                    Debug.Log($"Couldn't generate impostor for {target.name} because can't resolve the destination");
                    continue;
                }
                
                directory = Path.Combine(directory, $"Impostor_{filename}");

                if (!Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                var impostorSources = GatherImpostorSources(target);

                if (impostorSources.Length == 0)
                {
                    Debug.Log("Impostor Target doesn't have any mesh renderers or they have no valid data (material or mesh), aborting...");
                    return;
                }

                Material customOutputMaterial = m_CustomOutputMaterial;

                if (customOutputMaterial != null)
                {
                    for (int i = 0; i < impostorSources.Length; ++i)
                    {
                        var impostor = impostorSources[i];
                        var customMat = Instantiate(customOutputMaterial);
                        customMat.CopyMatchingPropertiesFromMaterial(impostor.mat);
                        impostor.customOutputMaterial = customMat;
                        impostorSources[i] = impostor;
                    }
                }

                var renderer = new DefaultImpostorRenderer();
                renderer.SetImpostorSources(impostorSources);
                OctaImpostorGenerator.GenerateImpostorSettings genSettings;
                genSettings.frameCount = m_FrameCount;
                genSettings.frameResolution = m_PerFrameResolution;
                genSettings.produceSpecularSmoothness = m_GenerateSpecSmoothness;
                genSettings.useHemiOcta = m_UseHemiOcta;
                genSettings.writeTexturesAsPNG = m_WriteTexturesAsPNG;
                genSettings.cameraDistance = m_CameraDistanceFromTarget;
                genSettings.alphaBlurRatio = m_AlphaBlurRadius;
                genSettings.directoryPath = directory;
                genSettings.impostorName = filename;
                genSettings.renderer = renderer;
                genSettings.overrideShader = null;
                genSettings.applyBoundsOffsetToMesh = true;
                genSettings.customPostProcessAssetsCB = null;
                genSettings.generatorListener = new DefaultGeneratorUI();
                genSettings.meshType = OctaImpostorGenerator.ImpostorMeshType.Octa;
                genSettings.customTextureDefinition = m_CustomOutputMaterial == null ? null : new OctaImpostorGenerator.CustomTextureDefinition()
                {
                    CustomTexturePropertyNameInImpostorMaterial = m_CustomOutputImpostorPropertyName
                };
                

                float3 boundsMargin = new float3(m_BoundsExtraMargin, m_BoundsExtraMargin, m_BoundsExtraMargin);
                
                genSettings.renderer.SetBoundsExtraMargin(boundsMargin);

                bool addToLODGroup = m_BakeIntoLODGroup && target.TryGetComponent(out LODGroup lodGroup);
                if (addToLODGroup)
                {
                    genSettings.position = float3.zero;
                    genSettings.rotation = quaternion.identity;
                    genSettings.scale = new float3(1.0f, 1.0f, 1.0f);
                    genSettings.prefabToClone =  target;
                }
                else
                {
                    genSettings.position = target.transform.localPosition;
                    genSettings.rotation = target.transform.localRotation;
                    genSettings.scale = target.transform.localScale;
                    genSettings.prefabToClone =  null;
                }

                var outputPath = m_Generator.GenerateImpostor(genSettings);
                if(outputPath != null)
                {
                    Debug.Log($"Generated Impostor for {target.name} to <a imposter=\"{outputPath}\"><b> {outputPath} </b></a>");
                }
                
            }
            
            

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
        
        void DrawVisualization()
        {
            EditorGUILayout.Space();
            m_ShowVisualization = EditorGUILayout.BeginFoldoutHeaderGroup(m_ShowVisualization, "Visualization");
            if (!m_ShowVisualization)
            {
                EditorGUILayout.EndFoldoutHeaderGroup();
                return;
            }
            if (m_Targets.Length == 0) return;

            m_VisDrawImpostorLocations = EditorGUILayout.Toggle("Draw Impostor Camera Locations", m_VisDrawImpostorLocations);
            m_VisEntryIndexToVisualize = Mathf.Clamp(EditorGUILayout.IntField("Entry Index", m_VisEntryIndexToVisualize), 0, m_Targets.Length - 1);
            
            var target = m_Targets[m_VisEntryIndexToVisualize];
            
            if(target == null) return;

            var impostorSources = GatherImpostorSources(target);
            
            if (impostorSources.Length > 0)
            {
                var toRootTransform = target.transform.worldToLocalMatrix;

                DefaultImpostorRenderer renderer = new DefaultImpostorRenderer();
                renderer.SetImpostorSources(impostorSources);
                
                var rect = GUILayoutUtility.GetAspectRect(16.0f / 9.0f, EditorStyles.helpBox, GUILayout.MaxHeight(400.0f));
                if (rect.width >= 20.0f)
                {
                    rect = EditorGUI.IndentedRect(rect);

                    GUI.Box(rect, Texture2D.blackTexture, EditorStyles.textField);
                    {
                        rect.xMin += 1;
                        rect.yMin += 1;
                        rect.xMax -= 1;
                        rect.yMax -= 1;
                    }
                    
                    //setup camera
                    float markerScaler = 0.1f;
                    float markerRelativeDistance = 2.0f;
                    Bounds ImpostorSourceBounds = renderer.GetBounds().ToBounds();
                    Bounds bounds = ImpostorSourceBounds;
                    float markerDistance = math.length(ImpostorSourceBounds.extents) * markerRelativeDistance;
                    if (m_VisDrawImpostorLocations)
                    {
                        float3 center = bounds.center;
                        for (int x = 0; x < m_FrameCount.x; ++x)
                        {
                            for (int y = 0; y < m_FrameCount.y; ++y)
                            {
                                float u = (float)x / m_FrameCount.x;
                                float v = (float)y / m_FrameCount.y;
                                float3 direction = m_UseHemiOcta ? ImpostorGeneratorUtils.HemiOctDecode(new float2(u,v)) : ImpostorGeneratorUtils.OctDecode(new float2(u,v));
                                direction = math.normalize(direction);
                                float3 offsetFromOrigo = center + markerDistance * direction;
                                bounds.Encapsulate(offsetFromOrigo);
                            }
                        }
                    }
                    
                    
                    float nearPlane;
                    float farPlane;
                    float fovX;
                    float fovY;
                    float camDistanceFromBoundsCenter;
                    ImpostorGeneratorUtils.CalculateCameraParametersAroundBounds(bounds.ToAABB(), m_CameraDistanceFromTarget, out nearPlane, out farPlane,out fovX, out fovY, out camDistanceFromBoundsCenter);
                    m_PreviewRenderer.camera.nearClipPlane = nearPlane;
                    m_PreviewRenderer.camera.farClipPlane = farPlane;
                    m_PreviewRenderer.camera.fieldOfView = fovY * math.TODEGREES;
                    m_PreviewRenderer.camera.aspect = fovX / fovY;
                    m_PreviewRenderer.camera.transform.position = (Vector3)bounds.center + camDistanceFromBoundsCenter * -Vector3.forward;
                    m_PreviewRenderer.camera.transform.LookAt(bounds.center, Vector3.up);
                    
                    m_PreviewRenderer.BeginPreview(rect, GUIStyle.none);
                    
                    renderer.Render(m_PreviewRenderer, IImpostorRenderer.RenderPass.Albedo);

                    if (m_VisDrawImpostorLocations)
                    {
                        for (int x = 0; x < m_FrameCount.x; ++x)
                        {
                            for (int y = 0; y < m_FrameCount.y; ++y)
                            {
                                float u = (float)x / (m_FrameCount.x - 1);
                                float v = (float)y / (m_FrameCount.y - 1);
                                float3 direction = m_UseHemiOcta ? ImpostorGeneratorUtils.HemiOctDecode(new float2(u,v)) : ImpostorGeneratorUtils.OctDecode(new float2(u,v));
                                direction = math.normalize(direction);
                                float3 offsetFromOrigo = (float3)ImpostorSourceBounds.center + markerDistance * direction;
                                Matrix4x4 markerTransform = Matrix4x4.TRS(offsetFromOrigo, Quaternion.identity, Vector3.one * markerScaler );
                                
                                m_VisMatPropertyBlock.SetColor("_Color", new Color(u, v, 0, 1));
                                
                                m_PreviewRenderer.DrawMesh(m_VisFrameMarkerMesh, markerTransform, m_VisFrameMarkerMaterial, 0, m_VisMatPropertyBlock);
                            }
                        }
                    }
                    m_PreviewRenderer.Render(true, false);
                    m_PreviewRenderer.EndAndDrawPreview(rect);
                }
            }
            
            EditorGUILayout.EndFoldoutHeaderGroup();
        }

        
    }

}