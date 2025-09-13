using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEditor;
using UnityEditor.VersionControl;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Serialization;
using Object = UnityEngine.Object;

namespace TimeGhost
{
    public interface IOctaImpostorGeneratorListener
    {
        public void OnBeginFrameRender(OctaImpostorGenerator.ImpostorSettings settings, uint2 frameBeingRendered);
    }
    
    public class OctaImpostorGenerator
    {
        public enum ImpostorMeshType
        {
            Quad,
            TightenedQuad,
            Octa
        }
        
        public struct GenerateImpostorSettings
        {
            public uint2 frameCount;
            public uint2 frameResolution;
            public bool produceSpecularSmoothness;
            public bool useHemiOcta;
            public float cameraDistance;
            public float alphaBlurRatio;
            public string directoryPath;
            public string impostorName;
            public ImpostorMeshType meshType;
            public IImpostorRenderer renderer;
            public IOctaImpostorGeneratorListener generatorListener;
            public float3 position;
            public Quaternion rotation;
            public float3 scale;
            public GameObject prefabToClone;
            public Shader overrideShader;
            public bool applyBoundsOffsetToMesh;
            public bool writeTexturesAsPNG;
            public Action<GameObject, Material, Mesh, IImpostorContext.ImpostorTexture[]> customPostProcessAssetsCB;
            public CustomTextureDefinition customTextureDefinition;
        }
        
        public struct ImpostorSettings
        {
            public uint2 frameResolution;
            public uint2 frameCount;
            public bool hemiOcta;
            public IImpostorContext.CameraSettings cameraSettings;
            public float cameraExtraDistance;
            public uint frameDilateLengthInTexels;
            public float alphaSearchRadius;
            public bool applyBoundsOffsetToMesh;
        }

        

        public class CustomTextureDefinition
        {
            public string CustomTexturePropertyNameInImpostorMaterial;
        }

        public struct ImpostorTextureAtlas
        {
            public RenderTexture albedoAlpha;
            public RenderTexture normalDepth;
            public RenderTexture specSmoothness;
            public RenderTexture cutout;
            public RenderTexture customOutput;
            
            public void Release()
            {
                DestroyImpostorAtlas(this);
            }
        }

        private IImpostorContext m_Context = null;

        public static ImpostorTextureAtlas CreateImpostorAtlas(uint2 atlasResolution, bool produceSpecularSmoothness, bool produceCustomTexture)
        {
            uint atlasResolutionX = atlasResolution.x;
            uint atlasResolutionY = atlasResolution.y;
            RenderTextureDescriptor descColorAlpha = ImpostorGeneratorUtils.GetRenderTextureDesc(atlasResolutionX, atlasResolutionY, RenderTextureFormat.ARGB32);
            RenderTextureDescriptor descNormalDepth = ImpostorGeneratorUtils.GetRenderTextureDesc(atlasResolutionX, atlasResolutionY, RenderTextureFormat.ARGB32);
            RenderTextureDescriptor descSpecSmoothness = ImpostorGeneratorUtils.GetRenderTextureDesc(atlasResolutionX, atlasResolutionY, RenderTextureFormat.ARGB32);
            RenderTextureDescriptor descCutout = ImpostorGeneratorUtils.GetRenderTextureDesc(atlasResolutionX, atlasResolutionY, RenderTextureFormat.R8);
            RenderTextureDescriptor customTexture = ImpostorGeneratorUtils.GetRenderTextureDesc(atlasResolutionX, atlasResolutionY,  RenderTextureFormat.ARGB32);

            ImpostorTextureAtlas atlas = new ImpostorTextureAtlas()
            {
                albedoAlpha = new RenderTexture(descColorAlpha),
                normalDepth = new RenderTexture(descNormalDepth),
                specSmoothness = produceSpecularSmoothness ? new RenderTexture(descSpecSmoothness) : null,
                customOutput = produceCustomTexture ? new RenderTexture(customTexture) : null,
                cutout = new RenderTexture(descCutout)
            };
            atlas.albedoAlpha.Create();
            atlas.normalDepth.Create();
            atlas.cutout.Create();
            
            if (produceSpecularSmoothness)
            {
                atlas.specSmoothness.Create();
            }

            if (produceCustomTexture)
            {
                atlas.customOutput.Create();
            }
            
            return atlas;
        }

        public static ImpostorTextureAtlas CreateImpostorAtlas(in ImpostorSettings settings,  bool produceSpecularSmoothness, bool produceCustomTexture)
        {
            return CreateImpostorAtlas(settings.frameCount * settings.frameResolution, produceSpecularSmoothness, produceCustomTexture);
        }

        public static void DestroyImpostorAtlas(in ImpostorTextureAtlas atlas)
        {
            if (ImpostorGeneratorUtils.IsAllocated(atlas.albedoAlpha))
            {
                atlas.albedoAlpha.Release();
            }

            if (ImpostorGeneratorUtils.IsAllocated(atlas.normalDepth))
            {
                atlas.normalDepth.Release();
            }

            if (ImpostorGeneratorUtils.IsAllocated(atlas.specSmoothness))
            {
                atlas.specSmoothness.Release();
            }
            
            if (ImpostorGeneratorUtils.IsAllocated(atlas.cutout))
            {
                atlas.cutout.Release();
            }

            if (ImpostorGeneratorUtils.IsAllocated(atlas.customOutput))
            {
                atlas.customOutput.Release();
            }
        }

        public void Init()
        {
            if (m_Context == null)
            {
                m_Context = new ImpostorContextHdrp(); //for now only support HDRP
                m_Context.Init();
            }
        }

        public void Deinit()
        {
            if (m_Context != null)
            {
                m_Context.Deinit();
            }
        }

        public string GenerateImpostor(GenerateImpostorSettings genSettings)
        {
            var bounds = genSettings.renderer.GetBounds();
            float nearPlane;
            float farPlane;
            float fovX;
            float fovY;
            float camDistanceFromBoundsCenter;
            ImpostorGeneratorUtils.CalculateCameraParametersAroundBounds(bounds, genSettings.cameraDistance, out nearPlane, out farPlane, out fovX, out fovY, out camDistanceFromBoundsCenter);

            ImpostorSettings settings = new ImpostorSettings()
            {
                frameResolution = genSettings.frameResolution,
                frameCount = genSettings.frameCount,
                cameraExtraDistance = genSettings.cameraDistance,
                hemiOcta = genSettings.useHemiOcta,
                frameDilateLengthInTexels = 50,
                alphaSearchRadius = genSettings.alphaBlurRatio,
                applyBoundsOffsetToMesh = genSettings.applyBoundsOffsetToMesh,
                cameraSettings = new IImpostorContext.CameraSettings()
                {
                    farPlane = farPlane,
                    nearPlane = nearPlane,
                    fovYOrOrtoWidth = fovY,
                    fovXOrOrthoHeight = fovX,
                    useOrthoGraphicProj = false
                }
            };

            IImpostorContext.ImpostorTexture[] impostorTextures = CreateImpostorTextures(settings, genSettings.renderer,
                genSettings.generatorListener, genSettings.produceSpecularSmoothness, genSettings.customTextureDefinition != null);
            
            Texture2D cutoutTex = null;
            foreach (var impostorTexture in impostorTextures)
            {
                if (impostorTexture.type == IImpostorContext.ImpostorTextureType.Cutout)
                {
                    cutoutTex = impostorTexture.texture;
                    break;
                }
            }
            Mesh impostorMesh = CreateImpostorMesh(settings, bounds, cutoutTex, genSettings.meshType);

            if (impostorMesh == null)
            {
                Debug.Log($"Failed To create impostor {genSettings.impostorName}. This can be caused by the impostor being too tiny to be captured in the specified resolution.");
                return null;
            }
            
            //serialize
            Shader impostorShader = genSettings.overrideShader != null ? genSettings.overrideShader : Resources.Load<Shader>("ImpostorRuntime/OctahedralImpostor");
            
            string prefabPath = Path.Combine(genSettings.directoryPath, genSettings.impostorName + ".prefab");
            string meshPath = Path.Combine(genSettings.directoryPath, genSettings.impostorName + ".mesh");
            string materialPath = Path.Combine(genSettings.directoryPath, genSettings.impostorName + ".mat");
            string texturePathSuffix = Path.Combine(genSettings.directoryPath, genSettings.impostorName);

            //Delete old combined asset
            {
                string assetsPath = Path.Combine(genSettings.directoryPath, genSettings.impostorName + ".asset");
                if (AssetDatabase.AssetPathExists(assetsPath))
                {
                    AssetDatabase.DeleteAsset(assetsPath);
                }
            }


            
            GameObject impostorGO = new GameObject(genSettings.impostorName);
            Material impostorMaterial = null;
            
            //check if there is an existing material as we want to preserve the potential changes done to it
            if (AssetDatabase.AssetPathExists(materialPath))
            {
                impostorMaterial = Object.Instantiate(AssetDatabase.LoadAssetAtPath<Material>(materialPath));
            }
            
            
            if (impostorMaterial == null)
            {
                impostorMaterial = new Material(impostorShader);
            }
            else
            {
                impostorMaterial.shader = impostorShader;
            }
            impostorMaterial.name = genSettings.impostorName;


            if (genSettings.customPostProcessAssetsCB != null)
            {
                genSettings.customPostProcessAssetsCB(impostorGO, impostorMaterial, impostorMesh, impostorTextures);
            }
            

            //serialize textures and replace the handle with serialzied texture handle
            for (int i = 0; i < impostorTextures.Length; ++i)
            {
                var impostorTexture = impostorTextures[i];
                switch (impostorTexture.type)
                {
                    case IImpostorContext.ImpostorTextureType.AlbedoAlpha:
                        impostorTexture.texture = ImpostorGeneratorUtils.WriteTextureToDisk(texturePathSuffix + "_AlbedoAlpha", impostorTexture.texture, genSettings.writeTexturesAsPNG);
                        break;
                    case IImpostorContext.ImpostorTextureType.NormalDepth:
                        impostorTexture.texture = ImpostorGeneratorUtils.WriteTextureToDisk(texturePathSuffix + "_NormalDepth", impostorTexture.texture, genSettings.writeTexturesAsPNG);
                        break;
                    case IImpostorContext.ImpostorTextureType.SpecularSmoothness:
                        impostorTexture.texture = ImpostorGeneratorUtils.WriteTextureToDisk(texturePathSuffix + "_SpecSmoothness", impostorTexture.texture, genSettings.writeTexturesAsPNG);
                        break;
                    case IImpostorContext.ImpostorTextureType.Custom:
                        impostorTexture.texture = ImpostorGeneratorUtils.WriteTextureToDisk(texturePathSuffix + "_Custom", impostorTexture.texture, genSettings.writeTexturesAsPNG);
                        break;
                }

                impostorTextures[i] = impostorTexture;
            }

            
            //material
            {
                float r = math.length(bounds.Size);
                float scalingFactor = 2.0f / r;
                foreach (var impostorTexture in impostorTextures)
                {
                    switch (impostorTexture.type)
                    {
                        case IImpostorContext.ImpostorTextureType.AlbedoAlpha:
                            impostorMaterial.SetTexture("_ColorAlphaAtlas", impostorTexture.texture);
                            break;
                        case IImpostorContext.ImpostorTextureType.NormalDepth:
                            impostorMaterial.SetTexture("_NormalDepthAtlas", impostorTexture.texture);
                            break;
                        case IImpostorContext.ImpostorTextureType.SpecularSmoothness:
                            impostorMaterial.SetTexture("_SpecularSmoothnessAtlas", impostorTexture.texture);
                            break;
                        case IImpostorContext.ImpostorTextureType.Custom:
                            if (!string.IsNullOrEmpty(genSettings.customTextureDefinition.CustomTexturePropertyNameInImpostorMaterial))
                            {
                                impostorMaterial.SetTexture(genSettings.customTextureDefinition.CustomTexturePropertyNameInImpostorMaterial, impostorTexture.texture);
                            }
                            break;
                    }
                }
                impostorMaterial.SetVector("_SpriteGridResolution", new Vector4(settings.frameCount.x, settings.frameCount.y, 0 ,0));
                impostorMaterial.SetInt("_HemiOctahedron", settings.hemiOcta ? 1 : 0);
                impostorMaterial.SetFloat("_AlphaClip", 0.5f);
                impostorMaterial.SetFloat("_ProjectionScale", scalingFactor);
                impostorMaterial.SetVector("_ImpostorCenterOS", genSettings.applyBoundsOffsetToMesh ?  new Vector4(bounds.Center.x, bounds.Center.y, bounds.Center.z) : Vector4.zero);
                impostorMaterial.SetFloat("_impostorDepthSpan", farPlane - nearPlane);
                
                impostorMaterial.SetKeyword(new LocalKeyword(impostorMaterial.shader, "_SPECULARSMOOTHNESSATLAS_ON"), genSettings.produceSpecularSmoothness);
                impostorMaterial.SetFloat("_SPECULARSMOOTHNESSATLAS", genSettings.produceSpecularSmoothness ? 1 : 0);

                EditorUtility.SetDirty(impostorMaterial);
                
            }

            ImpostorGeneratorUtils.ForceCreateAsset(materialPath, impostorMaterial);
            ImpostorGeneratorUtils.ForceCreateAsset(meshPath, impostorMesh);

            AssetDatabase.ImportAsset(materialPath, ImportAssetOptions.ForceUpdate);
            AssetDatabase.ImportAsset(meshPath, ImportAssetOptions.ForceUpdate);
            impostorMaterial = AssetDatabase.LoadAssetAtPath<Material>(materialPath);
            impostorMesh = AssetDatabase.LoadAssetAtPath<Mesh>(meshPath);
            
            {
                impostorGO.transform.localPosition = genSettings.position;
                impostorGO.transform.localScale = genSettings.scale;
                impostorGO.transform.localRotation = genSettings.rotation;
                var mf = impostorGO.AddComponent<MeshFilter>();
                var mr = impostorGO.AddComponent<MeshRenderer>();
                mf.sharedMesh = impostorMesh;
                mr.sharedMaterial = impostorMaterial;

                if (genSettings.prefabToClone != null)
                {
                    AddImpostorToPrefabAndCreateVariant(genSettings.prefabToClone, impostorGO, prefabPath);
                }
                else
                {
                    if (AssetDatabase.AssetPathExists(prefabPath))
                    {
                        AssetDatabase.DeleteAsset(prefabPath);
                    }
                    PrefabUtility.SaveAsPrefabAsset(impostorGO, prefabPath);
                    Object.DestroyImmediate(impostorGO);
                }
            }
            
            AssetDatabase.SaveAssets();
            return prefabPath;
        }

        public GameObject AddImpostorToPrefabAndCreateVariant(GameObject cloneSource, GameObject impostorGO, string outputPath)
        {
            GameObject prefab;
            if (PrefabUtility.IsPartOfPrefabInstance(cloneSource))
            {
                var path = PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(cloneSource);
                Object prefabAsset;
                if(Path.GetExtension(path).Equals("prefab"))
                {
                    prefabAsset = PrefabUtility.InstantiatePrefab(PrefabUtility.LoadPrefabContents(path));
                    
                } 
                else
                {
                    prefabAsset = AssetDatabase.LoadAssetAtPath<Object>(path);
                }

                prefab = (GameObject)PrefabUtility.InstantiatePrefab(prefabAsset);
            }
            else
            {
                prefab = Object.Instantiate(cloneSource);
            }
            
            impostorGO.transform.parent = prefab.transform;

            if (prefab.TryGetComponent(out LODGroup lod))
            {
                var lods = lod.GetLODs();
                
                var lastLod = lods[^1];

                float transitionHeight = lastLod.screenRelativeTransitionHeight;

                if (lods.Length > 1)
                {
                    var secondLastLod = lods[^2];
                    float previousLastLodtransitionHeight = secondLastLod.screenRelativeTransitionHeight * 0.5f + lastLod.screenRelativeTransitionHeight * 0.5f;
                    lastLod.screenRelativeTransitionHeight = previousLastLodtransitionHeight;
                    lods[^1] = lastLod;
                }
                
                
                LOD impostorLOD = new LOD()
                {
                    screenRelativeTransitionHeight = transitionHeight,
                    renderers = new[] { impostorGO.GetComponent<MeshRenderer>() },
                    fadeTransitionWidth = lastLod.fadeTransitionWidth
                };

                var newLODList = new LOD[lods.Length + 1];
                for (int i = 0; i < lods.Length; ++i)
                {
                    newLODList[i] = lods[i];
                }

                newLODList[^1] = impostorLOD;
                
                lod.SetLODs(newLODList);
            }
            
            GameObject obj = PrefabUtility.SaveAsPrefabAsset(prefab, outputPath);
            Object.DestroyImmediate(prefab);
            return obj;
        }

        public Mesh CreateImpostorMesh(in ImpostorSettings settings, AABB bounds, Texture2D cutoutTexture, ImpostorMeshType meshType)
        {
            Vector3[] positions = null;
            Vector3[] normals = null;
            Vector2[] uvs = null;
            int[] indices = null;

            if (cutoutTexture == null || meshType == ImpostorMeshType.Quad)
            {
                GetDefaultImpostorMeshData(out positions, out normals, out uvs, out indices);
            }
            else
            {
                bool success = GetCompactImpostorMeshData(out positions, out normals, out uvs, out indices, settings.frameResolution, settings.frameCount, cutoutTexture, meshType == ImpostorMeshType.Octa ? 8 : 4);
                if (!success)
                {
                    return null;
                }
            }
            
            //float maxDimension = math.max(math.max(bounds.Size.x, bounds.Size.y), bounds.Size.z);
            float scalingFactor = math.length(bounds.Size);
            for (int i = 0; i < positions.Length; ++i)
            {
                float3 c = settings.applyBoundsOffsetToMesh ? bounds.Center : float3.zero;
                positions[i] = (Vector3)c + positions[i] * scalingFactor;
            }
            
            var mesh = new Mesh();
            mesh.name = "ImpostorMesh";
            mesh.vertices = positions;
            mesh.normals = normals;
            mesh.uv = uvs;
            mesh.SetIndices(indices, MeshTopology.Triangles, 0);

            return mesh;
        }

        public IImpostorContext.ImpostorTexture[] CreateImpostorTextures(in ImpostorSettings settings, IImpostorRenderer renderer, IOctaImpostorGeneratorListener generatorListener, bool produceSpecularSmoothness, bool produceCustomOutput)
        {
            ImpostorTextureAtlas atlasResources = CreateImpostorAtlas(settings, produceSpecularSmoothness, produceCustomOutput);
            IImpostorContext.ImpostorFrameResources frameResources = IImpostorContext.ImpostorFrameResources.CreateImpostorFrameData(settings.frameResolution, produceSpecularSmoothness, produceCustomOutput);

            RenderImpostorAtlas(settings, frameResources, atlasResources, renderer, generatorListener);

            EditorUtility.DisplayProgressBar("OctaImpostorGenerator", $"Process atlas data", 0.0f);
            
            int atlasResolutionX = (int)(settings.frameResolution.x * settings.frameCount.x);
            int atlasResolutionY = (int)(settings.frameResolution.y * settings.frameCount.y);

            Texture2D albedoAlphaTex = new Texture2D(atlasResolutionX, atlasResolutionY, TextureFormat.RGBA32, false, true);
            albedoAlphaTex.name = "AlbedoAlpha";
            Texture2D normalDepthTex = new Texture2D(atlasResolutionX, atlasResolutionY, TextureFormat.RGBA32, false, true);
            normalDepthTex.name = "NormalDepth";
            Texture2D cutoutTex = new Texture2D(atlasResolutionX, atlasResolutionY, TextureFormat.R8, false, true);
            cutoutTex.name = "cutout";
            
            
            Texture2D specSmoothnessTex = null;
            if (produceSpecularSmoothness)
            {
                specSmoothnessTex = new Texture2D(atlasResolutionX, atlasResolutionY, TextureFormat.RGBA32, false, true);
                specSmoothnessTex.name = "specSmoothness";
            }
            
            Texture2D customTex = null;
            if (produceCustomOutput)
            {
                customTex = new Texture2D(atlasResolutionX, atlasResolutionY, TextureFormat.RGBA32, false, true);
                customTex.name = "customImpostorOutput";
            }
            
            {
                RenderTexture.active = atlasResources.albedoAlpha;
                albedoAlphaTex.ReadPixels(new Rect(0, 0, atlasResolutionX, atlasResolutionY), 0, 0, false);
                albedoAlphaTex.Apply();
                RenderTexture.active = null;
            }

            {
                RenderTexture.active = atlasResources.normalDepth;
                normalDepthTex.ReadPixels(new Rect(0, 0, atlasResolutionX, atlasResolutionY), 0, 0, false);
                normalDepthTex.Apply();
                RenderTexture.active = null;
            }

            {
                RenderTexture.active = atlasResources.cutout;
                cutoutTex.ReadPixels(new Rect(0, 0, atlasResolutionX, atlasResolutionY), 0, 0, false);
                cutoutTex.Apply();
                RenderTexture.active = null;
            }

            if (produceSpecularSmoothness)
            {
                RenderTexture.active = atlasResources.specSmoothness;
                specSmoothnessTex.ReadPixels(new Rect(0, 0, atlasResolutionX, atlasResolutionY), 0, 0, false);
                specSmoothnessTex.Apply();
                RenderTexture.active = null;
            }
            
            if (produceCustomOutput)
            {
                RenderTexture.active = atlasResources.customOutput;
                customTex.ReadPixels(new Rect(0, 0, atlasResolutionX, atlasResolutionY), 0, 0, false);
                customTex.Apply();
                RenderTexture.active = null;
            }
            

            IImpostorContext.ImpostorFrameResources.DestroyImpostorFrameData(frameResources);
            atlasResources.Release();
            
            List<IImpostorContext.ImpostorTexture> output = new List<IImpostorContext.ImpostorTexture>();
            output.Add(new IImpostorContext.ImpostorTexture{ type = IImpostorContext.ImpostorTextureType.AlbedoAlpha, texture = albedoAlphaTex });
            output.Add(new IImpostorContext.ImpostorTexture{ type = IImpostorContext.ImpostorTextureType.NormalDepth, texture = normalDepthTex });
            if (produceSpecularSmoothness)
            {
                output.Add(new IImpostorContext.ImpostorTexture{ type = IImpostorContext.ImpostorTextureType.SpecularSmoothness, texture = specSmoothnessTex });
            }

            if (produceCustomOutput)
            {
                output.Add(new IImpostorContext.ImpostorTexture{ type = IImpostorContext.ImpostorTextureType.Custom, texture = customTex });
            }

            output.Add(new IImpostorContext.ImpostorTexture{ type = IImpostorContext.ImpostorTextureType.Cutout, texture = cutoutTex });
            EditorUtility.ClearProgressBar();
            return output.ToArray();
        }

        public void RenderImpostorAtlas(in ImpostorSettings settings, in IImpostorContext.ImpostorFrameResources frameResources, in ImpostorTextureAtlas atlas, IImpostorRenderer renderer, IOctaImpostorGeneratorListener generatorListener)
        {

            AABB aabb = renderer.GetBounds();
            float r = math.length(aabb.Extents);

            IImpostorContext.ImpostorFrameSetup frameSetup;
            frameSetup.settings = settings.cameraSettings;
            for (uint x = 0; x < settings.frameCount.x; ++x)
            {
                for (uint y = 0; y < settings.frameCount.y; ++y)
                {
                    if (generatorListener != null)
                    {
                        generatorListener.OnBeginFrameRender(settings, new uint2(x, y));
                    }
                    float u = (float)x / (settings.frameCount.x - 1);
                    float v = (float)y / (settings.frameCount.y - 1);
                    float3 direction = settings.hemiOcta ? ImpostorGeneratorUtils.HemiOctDecode(new float2(u, v)) : ImpostorGeneratorUtils.OctDecode(new float2(u, v));
                    direction = math.normalize(direction);
                    float3 offsetFromOrigo = aabb.Center + (settings.cameraExtraDistance + r) * direction;

                    frameSetup.cameraPosition = offsetFromOrigo;
                    frameSetup.cameraDirection = math.normalize(aabb.Center - frameSetup.cameraPosition);

                    RenderFrame(frameSetup, frameResources, renderer);
                    ImpostorGeneratorUtils.PostProcessImpostorFrame(frameResources, (int)settings.frameDilateLengthInTexels, settings.alphaSearchRadius, true);
                    AssignFrame(new uint2(x, y), frameResources, atlas);
                }
            }
            
            EditorUtility.ClearProgressBar();
        }

        public void RenderFrame(in IImpostorContext.ImpostorFrameSetup frameSetup, in IImpostorContext.ImpostorFrameResources frameResources, IImpostorRenderer renderer)
        {
            m_Context.Render(frameSetup, frameResources, renderer);
        }

        

        public static void AssignFrame(uint2 frameIndices, in IImpostorContext.ImpostorFrameResources frameResources, in ImpostorTextureAtlas atlas)
        {
            var width = frameResources.albedoAlpha.width;
            var height = frameResources.albedoAlpha.height;

            int offsetX = (int)frameIndices.x * width;
            int offsetY = (int)frameIndices.y * height;

            int3 dispatchArgs = (int3)ImpostorRendererResources.GetDispatchArgs(ImpostorRendererResources.Kernels.kCopyToAtlas, new uint3((uint)width, (uint)height, 1));

            CommandBuffer cmd = CommandBufferPool.Get("ImpostorGen");

            cmd.SetComputeIntParams(ImpostorRendererResources.s_ComputeShader, ImpostorRendererResources.UniformIDs._TextureResolution, width, height);
            cmd.SetComputeIntParams(ImpostorRendererResources.s_ComputeShader, ImpostorRendererResources.UniformIDs._TextureOffset, offsetX, offsetY);

            cmd.SetComputeTextureParam(ImpostorRendererResources.s_ComputeShader, ImpostorRendererResources.Kernels.kCopyToAtlas, ImpostorRendererResources.UniformIDs._SourceTexture, frameResources.albedoAlpha);
            cmd.SetComputeTextureParam(ImpostorRendererResources.s_ComputeShader, ImpostorRendererResources.Kernels.kCopyToAtlas, ImpostorRendererResources.UniformIDs._TargetTexture, atlas.albedoAlpha);
            cmd.DispatchCompute(ImpostorRendererResources.s_ComputeShader, ImpostorRendererResources.Kernels.kCopyToAtlas, dispatchArgs.x, dispatchArgs.y, dispatchArgs.z);

            cmd.SetComputeTextureParam(ImpostorRendererResources.s_ComputeShader, ImpostorRendererResources.Kernels.kCopyToAtlas, ImpostorRendererResources.UniformIDs._SourceTexture, frameResources.normalDepth);
            cmd.SetComputeTextureParam(ImpostorRendererResources.s_ComputeShader, ImpostorRendererResources.Kernels.kCopyToAtlas, ImpostorRendererResources.UniformIDs._TargetTexture, atlas.normalDepth);
            cmd.DispatchCompute(ImpostorRendererResources.s_ComputeShader, ImpostorRendererResources.Kernels.kCopyToAtlas, dispatchArgs.x, dispatchArgs.y, dispatchArgs.z);

            if (ImpostorGeneratorUtils.IsAllocated(frameResources.specSmoothness) && ImpostorGeneratorUtils.IsAllocated(atlas.specSmoothness))
            {
                cmd.SetComputeTextureParam(ImpostorRendererResources.s_ComputeShader, ImpostorRendererResources.Kernels.kCopyToAtlas, ImpostorRendererResources.UniformIDs._SourceTexture, frameResources.specSmoothness);
                cmd.SetComputeTextureParam(ImpostorRendererResources.s_ComputeShader, ImpostorRendererResources.Kernels.kCopyToAtlas, ImpostorRendererResources.UniformIDs._TargetTexture, atlas.specSmoothness);
                cmd.DispatchCompute(ImpostorRendererResources.s_ComputeShader, ImpostorRendererResources.Kernels.kCopyToAtlas, dispatchArgs.x, dispatchArgs.y, dispatchArgs.z);
            }
            
            if (ImpostorGeneratorUtils.IsAllocated(frameResources.customOutput) && ImpostorGeneratorUtils.IsAllocated(atlas.customOutput))
            {
                cmd.SetComputeTextureParam(ImpostorRendererResources.s_ComputeShader, ImpostorRendererResources.Kernels.kCopyToAtlas, ImpostorRendererResources.UniformIDs._SourceTexture, frameResources.customOutput);
                cmd.SetComputeTextureParam(ImpostorRendererResources.s_ComputeShader, ImpostorRendererResources.Kernels.kCopyToAtlas, ImpostorRendererResources.UniformIDs._TargetTexture, atlas.customOutput);
                cmd.DispatchCompute(ImpostorRendererResources.s_ComputeShader, ImpostorRendererResources.Kernels.kCopyToAtlas, dispatchArgs.x, dispatchArgs.y, dispatchArgs.z);
            }


            cmd.SetComputeTextureParam(ImpostorRendererResources.s_ComputeShader, ImpostorRendererResources.Kernels.kCopyToAtlasCutout, ImpostorRendererResources.UniformIDs._SourceTexture, frameResources.cutout);
            cmd.SetComputeTextureParam(ImpostorRendererResources.s_ComputeShader, ImpostorRendererResources.Kernels.kCopyToAtlasCutout, ImpostorRendererResources.UniformIDs._TargetTexture, atlas.cutout);
            cmd.DispatchCompute(ImpostorRendererResources.s_ComputeShader, ImpostorRendererResources.Kernels.kCopyToAtlasCutout, dispatchArgs.x, dispatchArgs.y, dispatchArgs.z);
            

            Graphics.ExecuteCommandBuffer(cmd);
            CommandBufferPool.Release(cmd);
        }

        

        private static void GetDefaultImpostorMeshData(out Vector3[] positions, out Vector3[] normals, out Vector2[] uvs, out int[] indices)
        {
            positions = new[]
            {
                new Vector3(-0.5f, -0.5f, 0.0f),
                new Vector3(0.5f, -0.5f, 0.0f),
                new Vector3(-0.5f,  0.5f, 0.0f),
                new Vector3(0.5f, 0.5f, 0.0f)
            };

            Vector3 normal = new float3(0.0f, 0.0f, 1.0f);
            normals = new[] { normal, normal, normal, normal };
            uvs = new [] {
                new Vector2(0.0f, 0.0f),
                new Vector2(1.0f, 0.0f),
                new Vector2(0.0f, 1.0f),
                new Vector2(1.0f, 1.0f)
            };
            indices = new int[] { 0, 2, 1, 1, 2, 3 };
        }
        
        private static bool GetCompactImpostorMeshData(out Vector3[] positions, out Vector3[] normals, out Vector2[] uvs, out int[] indices, uint2 frameResolution, uint2 frameCounts, Texture2D cutoutTexture, int numberOfPlanes)
        {
            JobHandle jobHandle = default;
            unsafe
            {
                Color[] colors = cutoutTexture.GetPixels(0);
                NativeArray<int> mask = new NativeArray<int>((int)frameResolution.x * (int)frameResolution.y, Allocator.TempJob);
                
                fixed(Color* colPtr = colors)
                {
                    jobHandle = new GenerateAccumulatedFrameMask()
                    {
                        AccumulatedMask = mask,
                        AtlasColors = colPtr,
                        FrameCounts = (int2)frameCounts,
                        FrameResolution = (int2)frameResolution
                    }.Schedule(mask.Length, 32);
                    jobHandle.Complete();
                }

                bool success = ImpostorGeneratorUtils.GetCompactImpostorMeshData(out positions, out normals, out uvs, out indices, numberOfPlanes, frameResolution, mask);
                
                mask.Dispose(jobHandle);

                return success;
            }
        }

        [BurstCompile]
        private unsafe struct GenerateAccumulatedFrameMask : IJobParallelFor
        {
            [NativeDisableUnsafePtrRestriction]
            public Color* AtlasColors;
            public int2 FrameResolution;
            public int2 FrameCounts;

            [NativeDisableUnsafePtrRestriction]
            public NativeArray<int> AccumulatedMask;
            
            public void Execute(int index)
            {
                int atlasResX = FrameResolution.x * FrameCounts.x;
                int atlasResY = FrameResolution.y * FrameCounts.y;
                
                int xInFrame = index % FrameResolution.x;
                int yInFrame = index / FrameResolution.y;

                int maskValue = 0;
                
                for (int i = 0; i < FrameCounts.y; ++i)
                {
                    for (int k = 0; k < FrameCounts.x; ++k)
                    {
                        var x = k * FrameResolution.x + xInFrame;
                        var y = i * FrameResolution.y + yInFrame;

                        var atlasIndex = y * atlasResX + x;

                        Color col = AtlasColors[atlasIndex];
                        if (col.r > 0)
                        {
                            maskValue = 1;
                        }
                    }
                }

                AccumulatedMask[index] = maskValue;
            }
        }
        
    }
}