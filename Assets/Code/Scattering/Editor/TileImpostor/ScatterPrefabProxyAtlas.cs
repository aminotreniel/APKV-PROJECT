using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using Random = Unity.Mathematics.Random;
using SortableWeightEntry = TimeGhost.TileImpostorUtils.SortableWeightEntry<float>;
using SortableWeightComparer = TimeGhost.TileImpostorUtils.SortableWeightComparer<float>;

namespace TimeGhost
{
    public class ScatterPrefabProxyAtlas
    {
        private Dictionary<Tuple<Mesh, Material>, AtlasEntry> m_PrefabProxies = new Dictionary<Tuple<Mesh, Material>, AtlasEntry>();
        private DefaultImpostorRenderer m_PrefabProxyRenderer;
        private IImpostorContext m_Context;
        private int2 m_ProxyResolution = new int2(256, 256);
        private int2 m_AtlasMaxSize = new int2(2048, 2048);
        private Texture2D m_AtlasTextureAlbedoAlpha = null;
        private Texture2D m_AtlasTextureNormalDepth = null;

        private Tuple<Mesh, Material>[][] m_MostImportantEntriesPerCell = null;
        private Tuple<Mesh, Material>[] m_MostImportantEntriesGlobally = null;

        public class AtlasEntry
        {
            public int2 EntryIndex;
        }

        [BurstCompile]
        private unsafe struct CalculateBatchInfluence : IJob
        {
            [NativeDisableUnsafePtrRestriction] public Matrix4x4* Transforms;
            public int TransformCount;
            public AABB MeshBounds;
            public int IndexInInfluenceArray;
            [NativeDisableUnsafePtrRestriction] public SortableWeightEntry* Influence;

            public void Execute()
            {
                float sum = 0;
                for (int i = 0; i < TransformCount; ++i)
                {
                    var scaledBounds = Transforms[i].lossyScale * MeshBounds.Size;
                    sum += scaledBounds.x * scaledBounds.y * scaledBounds.z;
                }

                Influence[IndexInInfluenceArray] = new SortableWeightEntry() { value = sum, index = IndexInInfluenceArray };
            }
        }

        [BurstCompile]
        private unsafe struct SelectMostInfluentalBatches : IJob
        {
            public int NumberOfBatchesToPick;
            public int OffsetToIndices;
            public NativeArray<SortableWeightEntry> Influence;
            [NativeDisableUnsafePtrRestriction] public int* MostInfluentalIndicesOut;

            public void Execute()
            {
                SortableWeightComparer comp = new SortableWeightComparer();
                Influence.Sort(comp);

                for (int i = 0; i < NumberOfBatchesToPick; ++i)
                {
                    int index = -1;
                    if (i < Influence.Length)
                    {
                        index = Influence[i].index;
                    }

                    MostInfluentalIndicesOut[OffsetToIndices + i] = index;
                }
            }
        }

        public ScatterPrefabProxyAtlas()
        {
        }

        public void Init(int2 proxyFrameResolution, int2 maxProxyAtlasSize)
        {
            m_ProxyResolution = proxyFrameResolution;
            m_AtlasMaxSize = maxProxyAtlasSize;
            m_PrefabProxyRenderer = new DefaultImpostorRenderer();
            m_Context = new ImpostorContextHdrp();
            m_Context.Init();
        }

        public void Deinit()
        {
            m_PrefabProxyRenderer = null;
            m_Context.Deinit();
        }

        public Texture2D GetAtlasTextureAlbedoAlpha()
        {
            return m_AtlasTextureAlbedoAlpha;
        }

        public Texture2D GetAtlasTextureNormalDepth()
        {
            return m_AtlasTextureNormalDepth;
        }

        public AtlasEntry GetAtlasEntry(Mesh mesh, Material mat)
        {
            if (m_PrefabProxies.TryGetValue(new Tuple<Mesh, Material>(mesh, mat), out var entry))
            {
                return entry;
            }

            Debug.LogWarning("couldn't find atlas entry, returning a random default");
            return m_PrefabProxies.GetEnumerator().Current.Value;
        }

        public Tuple<Mesh, Material>[] GetMostImportantEntriesForCellIndex(int index)
        {
            return m_MostImportantEntriesPerCell[index];
        }

        public Tuple<Mesh, Material>[] GetMostImportantEntriesGlobally()
        {
            return m_MostImportantEntriesGlobally;
        }

        public AtlasEntry[] GetAtlasEntries(int index)
        {
            var entries = GetMostImportantEntriesForCellIndex(index);
            int entryCount = entries.Length;
            AtlasEntry[] atlasEntries = new AtlasEntry[entryCount];
            for (int i = 0; i < entries.Length; ++i)
            {
                atlasEntries[i] = GetAtlasEntry(entries[i].Item1, entries[i].Item2);
            }

            return atlasEntries;
        }

        public AtlasEntry[] GetMostImportantAtlasEntriesGlobally()
        {
            var entries = GetMostImportantEntriesGlobally();
            int entryCount = entries.Length;
            AtlasEntry[] atlasEntries = new AtlasEntry[entryCount];
            for (int i = 0; i < entries.Length; ++i)
            {
                atlasEntries[i] = GetAtlasEntry(entries[i].Item1, entries[i].Item2);
            }

            return atlasEntries;
        }

        public int2 GetProxyResolution()
        {
            return m_ProxyResolution;
        }


        public void CalculateMostImportantEntriesForAllCells(int numberOfEntries)
        {
            Dictionary<Tuple<Mesh, Material>, int> numberOfCellsUsingProxy = new Dictionary<Tuple<Mesh, Material>, int>();
            foreach (var perCell in m_MostImportantEntriesPerCell)
            {
                foreach (var entry in perCell)
                {
                    if (entry == null) continue;
                    if (numberOfCellsUsingProxy.TryGetValue(entry, out var count))
                    {
                        numberOfCellsUsingProxy[entry] = count + 1;
                    }
                    else
                    {
                        numberOfCellsUsingProxy.Add(entry, 1);
                    }
                }
            }

            var arr = numberOfCellsUsingProxy.ToArray();
            Array.Sort(arr, (x, y) => x.Value.CompareTo(y.Value));

            m_MostImportantEntriesGlobally = new Tuple<Mesh, Material>[math.min(numberOfEntries, arr.Length)];
            for (int i = 0; i < m_MostImportantEntriesGlobally.Length; ++i)
            {
                m_MostImportantEntriesGlobally[i] = arr[i].Key;
            }
        }

        public void CalculateMostImportantEntriesPerCell(int numberOfPrefabsPerTile, ImpostorScatteringRenderer.Batch[][] batchesPerCell)
        {
            NativeArray<int> mostImportantBatchIndices = new NativeArray<int>(numberOfPrefabsPerTile * batchesPerCell.Length, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);
            mostImportantBatchIndices.FillArray(-1);

            m_MostImportantEntriesPerCell = new Tuple<Mesh, Material>[batchesPerCell.Length][];

            unsafe
            {
                JobHandle jobHandle = default;
                for (int i = 0; i < batchesPerCell.Length; ++i)
                {
                    ImpostorScatteringRenderer.Batch[] batches = batchesPerCell[i];
                    if (batches == null) continue;
                    NativeArray<SortableWeightEntry> batchInfluence = new NativeArray<SortableWeightEntry>(batches.Length, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);

                    JobHandle combinedHandles = default;

                    for (int k = 0; k < batches.Length; ++k)
                    {
                        if (!batches[k].omitFromProxyAtlas)
                        {
                            fixed (Matrix4x4* matrices = batches[k].transforms)
                            {
                                var h = new CalculateBatchInfluence()
                                {
                                    MeshBounds = batches[k].mesh.bounds.ToAABB(),
                                    Transforms = matrices,
                                    TransformCount = batches[k].transforms.Length,
                                    IndexInInfluenceArray = k,
                                    Influence = (SortableWeightEntry*)batchInfluence.GetUnsafePtr(),
                                }.Schedule();

                                combinedHandles = JobHandle.CombineDependencies(h, combinedHandles);
                            }
                        }
                        else
                        {
                            batchInfluence[k] = new SortableWeightEntry()
                            {
                                index = k,
                                value = 0
                            };
                        }

                    }
                        

                    var selectHandle = new SelectMostInfluentalBatches()
                    {
                        Influence = batchInfluence,
                        MostInfluentalIndicesOut = (int*)mostImportantBatchIndices.GetUnsafePtr(),
                        NumberOfBatchesToPick = numberOfPrefabsPerTile,
                        OffsetToIndices = numberOfPrefabsPerTile * i
                    }.Schedule(combinedHandles);
                    batchInfluence.Dispose(selectHandle);

                    jobHandle = JobHandle.CombineDependencies(selectHandle, jobHandle);
                }

                jobHandle.Complete();
            }

            for (int i = 0; i < batchesPerCell.Length; ++i)
            {
                m_MostImportantEntriesPerCell[i] = new Tuple<Mesh, Material>[numberOfPrefabsPerTile];
                if (batchesPerCell[i] == null) continue;
                for (int k = 0; k < numberOfPrefabsPerTile; ++k)
                {
                    int batchIndex = mostImportantBatchIndices[i * numberOfPrefabsPerTile + k];
                    if (batchIndex < 0)
                    {
                        batchIndex = 0;
                    }

                    ImpostorScatteringRenderer.Batch b = batchesPerCell[i][batchIndex];
                    var key = new Tuple<Mesh, Material>(b.mesh, b.material);
                    m_MostImportantEntriesPerCell[i][k] = key;
                }
            }

            mostImportantBatchIndices.Dispose();
        }

        public void GenerateProxyAtlasForMostImportantEntries(string path, string impostorName)
        {
            m_PrefabProxies.Clear();
            for (int i = 0; i < m_MostImportantEntriesPerCell.Length; ++i)
            {
                for (int k = 0; k < m_MostImportantEntriesPerCell[i].Length; ++k)
                {
                    var key = m_MostImportantEntriesPerCell[i][k];
                    if (key == null) continue;
                    if (!m_PrefabProxies.TryGetValue(key, out var proxy))
                    {
                        m_PrefabProxies[key] = null;
                    }
                }
            }

            int proxyCount = m_PrefabProxies.Count;

            if (proxyCount == 0) return;

            int2 singleProxySize = m_ProxyResolution;
            int2 proxyCounts = new int2(m_AtlasMaxSize.x / singleProxySize.x, m_AtlasMaxSize.y / singleProxySize.y);

            //TODO: make atlas bigger or reduce the proxy size (or multiplie atlases). For now assume we always fit and just throw away enough entries if too much
            int proxiesThatFitConstraints = proxyCounts.x * proxyCounts.y;
            if (proxiesThatFitConstraints < proxyCount)
            {
                Debug.LogError($"Number of needed proxies ({proxyCount}) exceeds the constraints (number of proxies fitting the atlas ({proxiesThatFitConstraints})");
                return;
            }

            //shrink atlas
            var atlasSize = m_AtlasMaxSize;
            proxyCounts.y = (proxyCount + proxyCounts.x - 1) / proxyCounts.x;
            proxyCounts.x = math.min(proxyCount, proxyCounts.x);

            atlasSize.y = proxyCounts.y * singleProxySize.y;
            atlasSize.x = proxyCounts.x * singleProxySize.x;

            int proxyIndex = 0;
            var keys = m_PrefabProxies.Keys.ToArray();

            OctaImpostorGenerator.ImpostorTextureAtlas targetAtlas = OctaImpostorGenerator.CreateImpostorAtlas((uint2)atlasSize, false, false); //TODO: we might not need the maximum size

            var windProp = Shader.PropertyToID("_GlobalFoliageDebugDisableWind");
            var prevWindPropValue = Shader.GetGlobalFloat(windProp);

            Shader.SetGlobalFloat(windProp, 1);
            
            foreach (var key in keys)
            {
                int2 proxyIndices = new int2(proxyIndex % proxyCounts.x, proxyIndex / proxyCounts.x);

                //produce proxy to atlas
                {
                    int2 impostorIndex = proxyIndices;
                    CreateAtlasEntry(m_ProxyResolution, impostorIndex, key.Item1, key.Item2, ref targetAtlas, false);
                }

                m_PrefabProxies[key] = new AtlasEntry()
                {
                    EntryIndex = proxyIndices
                };

                ++proxyIndex;
            }
            Shader.SetGlobalFloat(windProp, prevWindPropValue);
            //write out atlas
            {
                var writeAsPNG = false;
                DownloadRelevantAtlasTextures(targetAtlas, out var albedoAlpha, out var normalDepth);
                string texturePathSuffix = Path.Combine(path, impostorName);
                m_AtlasTextureAlbedoAlpha = ImpostorGeneratorUtils.WriteTextureToDisk(texturePathSuffix + "_AlbedoAlphaProxyAtlas", albedoAlpha, writeAsPNG);
                m_AtlasTextureNormalDepth = ImpostorGeneratorUtils.WriteTextureToDisk(texturePathSuffix + "_NormalDepthProxyAtlas", normalDepth, writeAsPNG);
            }

            targetAtlas.Release();
        }

        private void DownloadRelevantAtlasTextures(OctaImpostorGenerator.ImpostorTextureAtlas atlas, out Texture2D albedoAlpha, out Texture2D normalDepth)
        {
            {
                int2 res = new int2(atlas.albedoAlpha.width, atlas.albedoAlpha.height);
                Texture2D albedoAlphaTex = new Texture2D(res.x, res.y, atlas.albedoAlpha.graphicsFormat, 0, TextureCreationFlags.DontInitializePixels);
                albedoAlphaTex.name = "AlbedoAlphaAtlas";
                {
                    RenderTexture.active = atlas.albedoAlpha;
                    albedoAlphaTex.ReadPixels(new Rect(0, 0, res.x, res.y), 0, 0, false);
                    albedoAlphaTex.Apply();
                    RenderTexture.active = null;
                    albedoAlpha = albedoAlphaTex;
                }
            }

            {
                int2 res = new int2(atlas.normalDepth.width, atlas.normalDepth.height);
                Texture2D normalDepthTex = new Texture2D(res.x, res.y, atlas.normalDepth.graphicsFormat, 0, TextureCreationFlags.DontInitializePixels);
                normalDepthTex.name = "NormalDepthAtlas";
                {
                    RenderTexture.active = atlas.normalDepth;
                    normalDepthTex.ReadPixels(new Rect(0, 0, res.x, res.y), 0, 0, false);
                    normalDepthTex.Apply();
                    RenderTexture.active = null;
                }
                normalDepth = normalDepthTex;
            }
        }

        public IImpostorContext.ImpostorFrameSetup CalculateFrameSetupFromAbove(AABB boundsWS, float camDistanceFromTarget, bool useOrthoGraphic)
        {
            float3 camDir = new float3(0.0f, -1.0f, 0.0f);

            float3 closestPoint = boundsWS.Extents.y * -camDir + boundsWS.Center;
            float3 cameraPosition = closestPoint + camDir * -camDistanceFromTarget;

            float nearPlane = math.length(cameraPosition - closestPoint);
            float farPlane = nearPlane + (boundsWS.Size.y);

            var frameSetup = new IImpostorContext.ImpostorFrameSetup();
            frameSetup.settings.nearPlane = nearPlane;
            frameSetup.settings.farPlane = farPlane;
            frameSetup.cameraDirection = camDir;
            frameSetup.cameraPosition = cameraPosition;
            frameSetup.settings.useOrthoGraphicProj = useOrthoGraphic;
            if (useOrthoGraphic)
            {
                frameSetup.settings.fovXOrOrthoHeight = boundsWS.Extents.x;
                frameSetup.settings.fovYOrOrtoWidth = boundsWS.Extents.z;
            }
            else
            {
                float fovXRadians = math.atan(boundsWS.Extents.x / nearPlane) * 2;
                float fovYRadians = math.atan(boundsWS.Extents.z / nearPlane) * 2;
                frameSetup.settings.fovXOrOrthoHeight = fovXRadians;
                frameSetup.settings.fovYOrOrtoWidth = fovYRadians;
            }

            return frameSetup;
        }

        public IImpostorContext.ImpostorFrameSetup CalculateFrameSetupFromSide(AABB boundsWS, float camDistanceFromTarget, bool useOrthoGraphic)
        {
            float3 camDir = new float3(0.0f, 0.0f, -1.0f);

            float3 closestPoint = boundsWS.Extents.z * -camDir + boundsWS.Center;
            float3 cameraPosition = closestPoint + camDir * -camDistanceFromTarget;

            float nearPlane = math.length(cameraPosition - closestPoint);
            float farPlane = nearPlane + (boundsWS.Size.z);

            var frameSetup = new IImpostorContext.ImpostorFrameSetup();
            frameSetup.settings.nearPlane = nearPlane;
            frameSetup.settings.farPlane = farPlane;
            frameSetup.cameraDirection = camDir;
            frameSetup.cameraPosition = cameraPosition;

            frameSetup.settings.useOrthoGraphicProj = useOrthoGraphic;
            if (useOrthoGraphic)
            {
                frameSetup.settings.fovXOrOrthoHeight = boundsWS.Extents.x;
                frameSetup.settings.fovYOrOrtoWidth = boundsWS.Extents.y;
            }
            else
            {
                float fovXRadians = math.atan(boundsWS.Extents.x / nearPlane) * 2;
                float fovYRadians = math.atan(boundsWS.Extents.y / nearPlane) * 2;
                frameSetup.settings.fovXOrOrthoHeight = fovXRadians;
                frameSetup.settings.fovYOrOrtoWidth = fovYRadians;
            }

            return frameSetup;
        }

        private void CreateAtlasEntry(int2 resolution, int2 frameIndex, Mesh mesh, Material mat, ref OctaImpostorGenerator.ImpostorTextureAtlas targetAtlas, bool fromAbove)
        {
            int numberOfProxiesToRender = fromAbove ? 32 : 8;
            float camDistance = 100.0f;


            bool isImpostor = mesh.bounds.extents.z < 0.0001f;
            var bounds = mesh.bounds.ToAABB();

            DefaultImpostorRenderer.ImpostorSource[] sources = new DefaultImpostorRenderer.ImpostorSource[numberOfProxiesToRender];
            //create new proxy
            {
                Random rand = new Random((uint)(1 + frameIndex.x + frameIndex.y));
                var meshBounds = mesh.bounds;
                for (int i = 0; i < numberOfProxiesToRender; ++i)
                {
                    float3 p = -(float3)meshBounds.extents + rand.NextFloat3() * meshBounds.size * 2.0f;
                    p.y *= 0.1f;
                    if (i == 0)
                    {
                        p = 0;
                    }

                    quaternion rotation = Quaternion.AngleAxis(rand.NextFloat() * 360.0f, Vector3.up); //* Quaternion.AngleAxis((rand.NextFloat() - 0.5f) * 30.0f, Vector3.right);
                    sources[i] = new DefaultImpostorRenderer.ImpostorSource()
                    {
                        mat = mat,
                        matPropBlock = null,
                        mesh = mesh,
                        transform = new float4x4(rotation, p)
                    };
                }

                m_PrefabProxyRenderer.SetImpostorSources(sources);

                if (isImpostor)
                {
                    bounds.Extents.z = math.min(bounds.Extents.x, bounds.Extents.y);
                    bounds.Extents += new float3(1, 1, 1);
                }

                IImpostorContext.ImpostorFrameSetup frameSetup;
                if (fromAbove)
                {
                    frameSetup = CalculateFrameSetupFromAbove(bounds, camDistance, false);
                }
                else
                {
                    frameSetup = CalculateFrameSetupFromSide(bounds, camDistance, false);
                }

                IImpostorContext.ImpostorFrameResources frameResources = IImpostorContext.ImpostorFrameResources.CreateImpostorFrameData((uint2)resolution, false, false);
                m_Context.Render(frameSetup, frameResources, m_PrefabProxyRenderer);
                ImpostorGeneratorUtils.ConvertAlbedoToMonochromatic(frameResources);
                ImpostorGeneratorUtils.PostProcessImpostorFrame(frameResources, 3, 1.0f / m_ProxyResolution.x, false);
                OctaImpostorGenerator.AssignFrame((uint2)frameIndex, frameResources, targetAtlas);
                frameResources.Release();
            }
        }
    }
}