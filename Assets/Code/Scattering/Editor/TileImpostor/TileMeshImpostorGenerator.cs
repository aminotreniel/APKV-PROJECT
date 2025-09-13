using System;
using System.Collections.Generic;
using System.IO;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEditor;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;
using Assert = UnityEngine.Assertions.Assert;
using Object = UnityEngine.Object;
using Random = Unity.Mathematics.Random;

namespace TimeGhost
{
    public class TileMeshImpostorGenerator
    {
        private IImpostorContext m_Context = null;

        private ScatterPrefabProxyAtlas m_ProxyAtlas;

        public void Init(ImpostorScatteringRenderer.Batch[][] batchesPerCell, int2 proxyFrameResolution,
            int2 maxProxyAtlasSize, string atlasDirectory, string atlasNamePrefix)
        {
            if (m_Context == null)
            {
                m_Context = new ImpostorContextHdrp(); //for now only support HDRP
                m_Context.Init();
            }

            if (m_ProxyAtlas == null)
            {
                m_ProxyAtlas = new ScatterPrefabProxyAtlas();
                m_ProxyAtlas.Init(proxyFrameResolution, maxProxyAtlasSize);
                m_ProxyAtlas.CalculateMostImportantEntriesPerCell(4, batchesPerCell);
                m_ProxyAtlas.GenerateProxyAtlasForMostImportantEntries(atlasDirectory, atlasNamePrefix);
                m_ProxyAtlas.CalculateMostImportantEntriesForAllCells(4);
            }
        }


        public void Deinit()
        {
            if (m_Context != null)
            {
                m_Context.Deinit();
                m_Context = null;
            }

            m_ProxyAtlas?.Deinit();
            m_ProxyAtlas = null;
        }


        private IImpostorContext.ImpostorTexture[] DownloadFrameResources(IImpostorContext.ImpostorFrameResources frame)
        {
            int2 res = new int2(frame.albedoAlpha.width, frame.albedoAlpha.height);

            Texture2D albedoAlphaTex = new Texture2D(res.x, res.y, frame.albedoAlpha.graphicsFormat, 0,
                TextureCreationFlags.DontInitializePixels);
            albedoAlphaTex.name = "AlbedoAlpha";
            Texture2D normalDepthTex = new Texture2D(res.x, res.y, frame.albedoAlpha.graphicsFormat, 0,
                TextureCreationFlags.DontInitializePixels);
            normalDepthTex.name = "NormalDepth";
            Texture2D cutoutTex = new Texture2D(res.x, res.y, frame.albedoAlpha.graphicsFormat, 0,
                TextureCreationFlags.DontInitializePixels);
            cutoutTex.name = "cutout";

            bool hasSpecSmoothness = ImpostorGeneratorUtils.IsAllocated(frame.specSmoothness);

            Texture2D specSmoothnessTex = null;
            if (hasSpecSmoothness)
            {
                specSmoothnessTex = new Texture2D(res.x, res.y, frame.albedoAlpha.graphicsFormat, 0,
                    TextureCreationFlags.DontInitializePixels);
                specSmoothnessTex.name = "specSmoothness";
            }

            {
                RenderTexture.active = frame.albedoAlpha;
                albedoAlphaTex.ReadPixels(new Rect(0, 0, res.x, res.y), 0, 0, false);
                albedoAlphaTex.Apply();
                RenderTexture.active = null;
            }

            {
                RenderTexture.active = frame.normalDepth;
                normalDepthTex.ReadPixels(new Rect(0, 0, res.x, res.y), 0, 0, false);
                normalDepthTex.Apply();
                RenderTexture.active = null;
            }

            {
                RenderTexture.active = frame.cutout;
                cutoutTex.ReadPixels(new Rect(0, 0, res.x, res.y), 0, 0, false);
                cutoutTex.Apply();
                RenderTexture.active = null;
            }

            if (hasSpecSmoothness)
            {
                RenderTexture.active = frame.specSmoothness;
                specSmoothnessTex.ReadPixels(new Rect(0, 0, res.x, res.y), 0, 0, false);
                specSmoothnessTex.Apply();
                RenderTexture.active = null;
            }

            List<IImpostorContext.ImpostorTexture> output = new List<IImpostorContext.ImpostorTexture>();
            output.Add(new IImpostorContext.ImpostorTexture
                { type = IImpostorContext.ImpostorTextureType.AlbedoAlpha, texture = albedoAlphaTex });
            output.Add(new IImpostorContext.ImpostorTexture
                { type = IImpostorContext.ImpostorTextureType.NormalDepth, texture = normalDepthTex });
            if (hasSpecSmoothness)
            {
                output.Add(new IImpostorContext.ImpostorTexture
                    { type = IImpostorContext.ImpostorTextureType.SpecularSmoothness, texture = specSmoothnessTex });
            }

            output.Add(new IImpostorContext.ImpostorTexture
                { type = IImpostorContext.ImpostorTextureType.Cutout, texture = cutoutTex });
            return output.ToArray();
        }

        private void CreateImpostorTextures(GenerateImpostorSettings genSettings, out IImpostorContext.ImpostorTexture[] impostorsOut, out IImpostorContext.ImpostorTexture[] downSampleStandardDev)
        {
            int2 renderResolution = new int2((int)genSettings.outputResolution.x << (int)genSettings.downSampleOutput, (int)genSettings.outputResolution.y << (int)genSettings.downSampleOutput);
            renderResolution = math.min(renderResolution, new int2(8192, 8192));

            IImpostorContext.ImpostorFrameResources frameResources = IImpostorContext.ImpostorFrameResources.CreateImpostorFrameData((uint2)renderResolution,
                    genSettings.produceSpecularSmoothness, false);

            var bounds = genSettings.renderer.GetBounds();
            IImpostorContext.ImpostorFrameSetup frameSetup = m_ProxyAtlas.CalculateFrameSetupFromAbove(bounds, genSettings.cameraDistance, true);
            m_Context.Render(frameSetup, frameResources, genSettings.renderer);

            bool varianceGenerated = genSettings.downSampleOutput > 0;

            IImpostorContext.ImpostorFrameResources sd = default;
            if (genSettings.downSampleOutput > 0)
            {
                IImpostorContext.ImpostorFrameResources downSampledFrame;
                ImpostorGeneratorUtils.DownSample(frameResources, (int)genSettings.downSampleOutput, true, out downSampledFrame, out sd);
                frameResources.Release();
                frameResources = downSampledFrame;
            }

            int2 outputResolution = (int2)genSettings.outputResolution;
            Assert.AreEqual(outputResolution, new int2(frameResources.albedoAlpha.width, frameResources.albedoAlpha.height));


            ImpostorGeneratorUtils.PostProcessImpostorFrame(frameResources, genSettings.textureDilationAmount, 0, false);

            impostorsOut = DownloadFrameResources(frameResources);
            frameResources.Release();

            if (varianceGenerated)
            {
                downSampleStandardDev = DownloadFrameResources(sd);
            }
            else
            {
                downSampleStandardDev = null;
            }


            EditorUtility.ClearProgressBar();
        }


        public string GenerateImpostor(GenerateImpostorSettings genSettings)
        {
            IImpostorContext.ImpostorTexture[] textures;
            IImpostorContext.ImpostorTexture[] varianceTextures;
            CreateImpostorTextures(genSettings, out textures, out varianceTextures);
            var bounds = genSettings.renderer.GetBounds();
            //check how many pixels are actually covered by something and if too low, just early out
            if (genSettings.impostorDiscardRatio > 0)
            {
                Texture2D cutoutTex = null;
                foreach (var impostorTexture in textures)
                {
                    if (impostorTexture.type == IImpostorContext.ImpostorTextureType.Cutout)
                    {
                        cutoutTex = impostorTexture.texture;
                        break;
                    }
                }

                int nonZeroTexels = 0;
                unsafe
                {
                    fixed (Color* col = cutoutTex.GetPixels())
                    {
                        UnsafeAtomicCounter32 counter =
                            new UnsafeAtomicCounter32(UnsafeUtility.Malloc(sizeof(int), 1, Allocator.Temp));
                        counter.Reset();
                        new CalculateTexelsWithNonZeroAlpha()
                        {
                            Counter = counter,
                            Colors = col
                        }.Schedule(cutoutTex.width * cutoutTex.height, 64).Complete();

                        nonZeroTexels = *counter.Counter;
                        UnsafeUtility.Free(counter.Counter, Allocator.Temp);
                    }
                }

                float nonZeroTexelsRatio = (float)nonZeroTexels / (cutoutTex.width * cutoutTex.height);
                if (genSettings.impostorDiscardRatio > nonZeroTexelsRatio)
                {
                    Debug.Log(
                        $"Discarding Impostor Tile (Bounds Center {bounds.Center}, Extents {bounds.Extents}) because the impostor only had {nonZeroTexels} nonzero alpha texels (ratio {nonZeroTexelsRatio})");
                    return null;
                }
            }

            string prefabPath = Path.Combine(genSettings.directoryPath, genSettings.impostorName + ".prefab");
            string meshPathPrefix = Path.Combine(genSettings.directoryPath, genSettings.impostorName);
            string materialPathPrefix = Path.Combine(genSettings.directoryPath, genSettings.impostorName);
            string texturePathSuffix = Path.Combine(genSettings.directoryPath, genSettings.impostorName);

            GameObject impostorGO = new GameObject(genSettings.impostorName);

            Texture2D depthTex = null;
            foreach (var impostorTexture in textures)
            {
                if (impostorTexture.type == IImpostorContext.ImpostorTextureType.NormalDepth)
                {
                    depthTex = impostorTexture.texture;
                    break;
                }
            }

            //find color variance if present
            Texture2D colorVariance = null;
            if (varianceTextures != null)
            {
                foreach (var tex in varianceTextures)
                {
                    if (tex.type == IImpostorContext.ImpostorTextureType.AlbedoAlpha)
                    {
                        colorVariance = tex.texture;
                        break;
                    }
                }
            }

            bool writeAsPNG = false;
            //serialize textures and replace the handle with serialzied texture handle
            for (int i = 0; i < textures.Length; ++i)
            {
                var impostorTexture = textures[i];
                switch (impostorTexture.type)
                {
                    case IImpostorContext.ImpostorTextureType.AlbedoAlpha:
                        impostorTexture.texture = ImpostorGeneratorUtils.WriteTextureToDisk(texturePathSuffix + "_AlbedoAlpha", impostorTexture.texture, writeAsPNG);
                        break;
                    case IImpostorContext.ImpostorTextureType.NormalDepth:
                        impostorTexture.texture = ImpostorGeneratorUtils.WriteTextureToDisk(texturePathSuffix + "_NormalDepth", impostorTexture.texture, writeAsPNG);
                        break;
                    case IImpostorContext.ImpostorTextureType.SpecularSmoothness:
                        impostorTexture.texture = ImpostorGeneratorUtils.WriteTextureToDisk(texturePathSuffix + "_SpecSmoothness", impostorTexture.texture, writeAsPNG);
                        break;
                }

                textures[i] = impostorTexture;
            }

            if (colorVariance != null)
            {
                colorVariance = ImpostorGeneratorUtils.WriteTextureToDisk(texturePathSuffix + "_ColorVariance",
                    colorVariance, writeAsPNG);
            }

            var globalEntries = m_ProxyAtlas.GetMostImportantAtlasEntriesGlobally();
            var localEntries = m_ProxyAtlas.GetAtlasEntries(genSettings.cellIndex);

            List<Tuple<Mesh, Material>> meshesAndMaterials = new List<Tuple<Mesh, Material>>();

            for (int meshDefIndex = 0; meshDefIndex < genSettings.meshDefinitions.Length; ++meshDefIndex)
            {
                var meshDef = genSettings.meshDefinitions[meshDefIndex];

                Shader defaultShader = meshDef.UseCards
                    ? Resources.Load<Shader>("TileImpostorShaderCards")
                    : Resources.Load<Shader>("TileImpostorShader");
                Shader impostorShader = genSettings.overrideShader != null ? genSettings.overrideShader : defaultShader;

                string materialPath = $"{materialPathPrefix}_LOD{meshDefIndex}.mat";
                string meshPath = $"{meshPathPrefix}_LOD{meshDefIndex}.mesh";

                //check if there is an existing material as we want to preserve the potential changes done to it
                Material impostorMaterial = null;
                if (AssetDatabase.AssetPathExists(materialPath))
                {
                    var asset = AssetDatabase.LoadAssetAtPath<Material>(materialPath);
                    if (asset != null)
                    {
                        impostorMaterial = Object.Instantiate(asset);
                    }
                }

                Mesh impostorMesh;
                if (meshDef.UseCards)
                {
                    int2 heightGridResolution = new int2((int)math.ceil(bounds.Size.x), (int)math.ceil(bounds.Size.z));
                    heightGridResolution = math.max(heightGridResolution, new int2(2, 2));
                    float sizeInMeters = bounds.Size.x * bounds.Size.z;
                    float vertices = meshDef.VerticesPerMeter * sizeInMeters;
                    int maxCardCount = (int)math.max(1.0f, vertices / 4);
                    impostorMesh = TileImpostorMeshUtility.CreateImpostorMeshScatteredCards(bounds, genSettings.renderer.GetBatches(), heightGridResolution, maxCardCount, meshDef.CardWidth, genSettings.positionMeshToBoundsCenter);
                }
                else
                {
                    int2 meshVertices = new int2((int)math.ceil(bounds.Size.x * meshDef.VerticesPerMeter), (int)math.ceil(bounds.Size.z * meshDef.VerticesPerMeter));
                    meshVertices = math.max(meshVertices, new int2(2, 2));
                    impostorMesh = TileImpostorMeshUtility.CreateImpostorMeshRectanglePattern(bounds, genSettings.renderer.GetBatches(), meshVertices, meshDef.VerticesPerMeter, genSettings.positionMeshToBoundsCenter);
                }


                if (impostorMaterial == null)
                {
                    impostorMaterial = new Material(impostorShader);
                }
                else
                {
                    impostorMaterial.shader = impostorShader;
                }

                impostorMaterial.name = $"{genSettings.impostorName}{meshDefIndex}_mat";
                


                float detailSize = 1.0f;

                //material
                {
                    impostorMaterial.SetMaterialType(MaterialId.LitTranslucent);
                    impostorMaterial.enableInstancing = true;

                    foreach (var impostorTexture in textures)
                    {
                        switch (impostorTexture.type)
                        {
                            case IImpostorContext.ImpostorTextureType.AlbedoAlpha:
                                impostorMaterial.SetTexture("_AlbedoAlphaTexture", impostorTexture.texture);
                                break;
                            case IImpostorContext.ImpostorTextureType.NormalDepth:
                                impostorMaterial.SetTexture("_NormalDepthTexture", impostorTexture.texture);
                                break;
                            case IImpostorContext.ImpostorTextureType.SpecularSmoothness:
                                impostorMaterial.SetTexture("_SpecularSmoothnessTexture", impostorTexture.texture);
                                break;
                        }
                    }

                    impostorMaterial.SetKeyword(new LocalKeyword(impostorMaterial.shader, "_SPECULARSMOOTHNESSTEXTURETOGGLE_ON"), genSettings.produceSpecularSmoothness);
                    impostorMaterial.SetFloat("_SPECULARSMOOTHNESSTEXTURETOGGLE", genSettings.produceSpecularSmoothness ? 1 : 0);
                    //impostorMaterial.SetFloat("_NoiseScale", math.max(bounds.Size.x, bounds.Size.z) / 5.0f);
                    impostorMaterial.SetFloat("_DetailUVMultiplier", math.max(bounds.Size.x, bounds.Size.z) / detailSize);
                    impostorMaterial.SetVector("_TileImpostorSize", new float4(bounds.Size.x, bounds.Size.y, bounds.Size.z, 0));
                    impostorMaterial.SetVector("_TileImpostorCardWidth", new float4(meshDef.CardWidth, 1.0f / meshDef.CardWidth, 0, 0));
                    impostorMaterial.SetFloat("_TileImpostorHeightBias", genSettings.impostorHeightBias);

                    {
                        var atlasTextureAlbedoAlpha = m_ProxyAtlas.GetAtlasTextureAlbedoAlpha();
                        var atlasTextureNormalDepth = m_ProxyAtlas.GetAtlasTextureNormalDepth();
                        int2 frameResolution = m_ProxyAtlas.GetProxyResolution();
                        float4 atlasRes = new float4(atlasTextureAlbedoAlpha.width, atlasTextureAlbedoAlpha.height, 1.0f / atlasTextureAlbedoAlpha.width, 1.0f / atlasTextureAlbedoAlpha.height);
                        float4 frameRes = new float4(frameResolution.x, frameResolution.y, 1.0f / frameResolution.x, 1.0f / frameResolution.y);
                        impostorMaterial.SetTexture("_ImpostorProxyAtlasAlbedoAlpha", atlasTextureAlbedoAlpha);
                        impostorMaterial.SetTexture("_ImpostorProxyAtlasNormalDepth", atlasTextureNormalDepth);
                        impostorMaterial.SetVector("_ImpostorProxyAtlasResolution", atlasRes);
                        impostorMaterial.SetVector("_ImpostorProxyAtlasEntryResolution", frameRes);
                        impostorMaterial.SetVector("_ImpostorProxyFrameIndices", new float4(globalEntries[0].EntryIndex.x, globalEntries[1].EntryIndex.x, globalEntries[2].EntryIndex.x, globalEntries[3].EntryIndex.x));
                    }

                    if (colorVariance != null)
                    {
                        impostorMaterial.SetTexture("_AlbedoVarianceTexture", colorVariance);
                    }
                    
                    EditorUtility.SetDirty(impostorMaterial);
                }

                ImpostorGeneratorUtils.ForceCreateAsset(materialPath, impostorMaterial);

                //need to add this after resource creation
                if (genSettings.overrideDiffusionProfile != null)
                {
                    HDMaterial.SetDiffusionProfileShaderGraph(impostorMaterial, genSettings.overrideDiffusionProfile, "_Diffusion_Profile");
                    EditorUtility.SetDirty(impostorMaterial);
                    AssetDatabase.SaveAssetIfDirty(impostorMaterial);
                }

                ImpostorGeneratorUtils.ForceCreateAsset(meshPath, impostorMesh);

                AssetDatabase.ImportAsset(materialPath, ImportAssetOptions.ForceUpdate);
                AssetDatabase.ImportAsset(meshPath, ImportAssetOptions.ForceUpdate);
                impostorMaterial = AssetDatabase.LoadAssetAtPath<Material>(materialPath);
                impostorMesh = AssetDatabase.LoadAssetAtPath<Mesh>(meshPath);
                
                meshesAndMaterials.Add(new Tuple<Mesh, Material>(impostorMesh, impostorMaterial));
            }


            {
                impostorGO.transform.localPosition = genSettings.position;
                impostorGO.transform.localScale = genSettings.scale;
                impostorGO.transform.localRotation = genSettings.rotation;
                
                List<GameObject> lods = new List<GameObject>();
                for (int i = 0; i < meshesAndMaterials.Count; ++i)
                {
                    var entry = meshesAndMaterials[i];
                    GameObject go;
                    if (i == 0)
                    {
                        go = impostorGO;
                    }
                    else
                    {
                        go = new GameObject();
                        go.transform.SetParent(impostorGO.transform, false);
                        lods.Add(go);
                    }
                    
                    var mf = go.AddComponent<MeshFilter>();
                    var mr = go.AddComponent<MeshRenderer>();
                    mf.sharedMesh = entry.Item1;
                    mr.sharedMaterial = entry.Item2;
                    
                }
                
                var tileImpostorPrefab = impostorGO.AddComponent<TileImpostorPrefab>();
                tileImpostorPrefab.LODs = lods.ToArray();

                int numberOfInstances = 0;
                var batches = genSettings.renderer.GetBatches();
                for (int i = 0; i < batches.Length; ++i)
                {
                    numberOfInstances += batches[i].transforms.Length;
                }

                tileImpostorPrefab.numberOfInstancesBaked = numberOfInstances;
                
                if (AssetDatabase.AssetPathExists(prefabPath))
                {
                    AssetDatabase.DeleteAsset(prefabPath);
                }

                PrefabUtility.SaveAsPrefabAsset(impostorGO, prefabPath);
                Object.DestroyImmediate(impostorGO);
            }

            AssetDatabase.SaveAssets();
            return prefabPath;
        }

        public struct GenerateImpostorSettings
        {
            public uint2 outputResolution;
            public uint downSampleOutput;
            public bool produceSpecularSmoothness;
            public int textureDilationAmount;
            public int alphaDistanceSearchInTexels;
            public float cameraDistance;
            public string directoryPath;
            public string impostorName;
            public int cellIndex;
            public float3 position;
            public Quaternion rotation;
            public float3 scale;
            public Shader overrideShader;
            public bool positionMeshToBoundsCenter;
            public float impostorDiscardRatio;
            public float impostorHeightBias;
            public DiffusionProfileSettings overrideDiffusionProfile;
            public BakedTileImpostorDataSet.ImpostorMeshDefinition[] meshDefinitions;
            public ImpostorScatteringRenderer renderer;
        }

        [BurstCompile]
        private unsafe struct CalculateTexelsWithNonZeroAlpha : IJobParallelFor
        {
            [NativeDisableUnsafePtrRestriction] public UnsafeAtomicCounter32 Counter;
            [NativeDisableUnsafePtrRestriction] public Color* Colors;

            public void Execute(int index)
            {
                if (Colors[index].r > 0)
                {
                    Counter.Add(1);
                }
            }
        }
    }
}