using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Mathematics;
using Unity.Profiling;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;
using Object = UnityEngine.Object;
using Random = Unity.Mathematics.Random;

namespace TimeGhost
{
    public class ScatterImpostorGeneratorUI : IOctaImpostorGeneratorListener
    {
        public int MaxCellCount = 1;
        public int CurrentCellIndex = 0;
        
        public void OnBeginFrameRender(OctaImpostorGenerator.ImpostorSettings settings, uint2 frameBeingRendered)
        {
            
            string msg = $"Render Scatter Impostors: Cell {CurrentCellIndex}/{MaxCellCount} (Frame x:{frameBeingRendered.x}/{settings.frameCount.x}, y:{frameBeingRendered.y}/{settings.frameCount.y})";


            float cellGenRatio = (float)CurrentCellIndex / MaxCellCount;
            float atlasGenRatio = (float)(frameBeingRendered.x * settings.frameCount.y + frameBeingRendered.y) / (settings.frameCount.x * settings.frameCount.y);
            
            EditorUtility.DisplayProgressBar("OctaImpostorGenerator", msg, cellGenRatio + (atlasGenRatio/MaxCellCount));
        }
    }
    
    public class ImpostorScatteringRenderer : IImpostorRenderer
    {
        public struct PrefabEntry
        {
            public Mesh mesh;
            public Material material;
            public Matrix4x4 rootTransform;
        }
        
        public struct PerInstanceData
        {
            public float2 _ScatteredInstanceExtraData;
        }
        
        private Bounds m_Bounds;

        public struct Batch
        {
            public Mesh mesh;
            public Material material;
            public Matrix4x4[] transforms;
            public PerInstanceData[] extraData;
            public bool omitFromProxyAtlas;
        }
        
        private List<Batch> m_Batches = new List<Batch>();
        private List<Material> m_CreatedMaterials = new List<Material>();
        private int m_BiggestBatch;

        private float3 m_ExtraMargin = float3.zero;
        private int m_NumberOfEntries;
        
        
        public ImpostorScatteringRenderer()
        {

            Clear();
        }

        public Batch[] GetBatches()
        {
            return m_Batches.ToArray();
        }

        public void AddBatches(Batch[] batches)
        {
            if(batches == null) return;
            foreach (var batch in batches)
            {
                AddBatch(batch);
            }
        }

        public void AddBatch(Batch batch)
        {
            var meshBounds = batch.mesh.bounds;
            foreach (var entry in batch.transforms)
            {
                var transformedBounds = ImpostorGeneratorUtils.TransformAABB(meshBounds, entry);
                m_Bounds.Encapsulate(transformedBounds);
            }

            if (!batch.material.enableInstancing)
            {
                batch.material = Object.Instantiate(batch.material);
                batch.material.enableInstancing = true;
                m_CreatedMaterials.Add(batch.material);
            }
            
            m_Batches.Add(batch);
            
            m_BiggestBatch = math.max(m_BiggestBatch, batch.transforms.Length);
            m_NumberOfEntries += batch.transforms.Length;

        }

        
        public void Clear()
        {
            m_Bounds.min = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
            m_Bounds.max = new Vector3(-float.MaxValue, -float.MaxValue, -float.MaxValue);
            m_Batches.Clear();
            foreach (var mat in m_CreatedMaterials)
            {
                Object.DestroyImmediate(mat);
            }
            m_CreatedMaterials.Clear();
            m_BiggestBatch = 0;
            m_NumberOfEntries = 0;
        }
        
        public void SetBoundsExtraMargin(float3 margin)
        {
            m_ExtraMargin = margin;
        }
        
        public AABB GetBounds()
        {
            AABB aabb = m_Bounds.ToAABB();
            aabb.Extents += m_ExtraMargin;
            return aabb;
        }

        public void Render(PreviewRenderUtility previewRenderer, IImpostorRenderer.RenderPass pass)
        {
            if (pass == IImpostorRenderer.RenderPass.Custom) return; //no support for custom pass
            MaterialPropertyBlock matPropBlock = new MaterialPropertyBlock();
            Random rand = new Random(12);
            const float maxRotationAngle = 75.0f;
            foreach (var batch in m_Batches)
            {
                for (int i = 0; i < batch.mesh.subMeshCount; ++i)
                {
                    for(int k = 0; k < batch.transforms.Length; ++k)
                    {
                        //random rotation
                        var quat = Quaternion.AngleAxis(rand.NextFloat() * maxRotationAngle, new Vector3(1.0f, 0.0f, 0.0f));
                        quat *= Quaternion.AngleAxis(rand.NextFloat() * maxRotationAngle, new Vector3(0.0f, 0.0f, 1.0f));

                        var transform = batch.transforms[k];
                        transform *= Matrix4x4.TRS(Vector3.zero, quat, Vector3.one);
                        
                        float2 instanceData = batch.extraData[k]._ScatteredInstanceExtraData;
                        matPropBlock.SetVector("_ScatteredInstanceExtraData", new Vector4(instanceData.x, instanceData.y, 0.0f, 0.0f));
                        previewRenderer.DrawMesh(batch.mesh, transform, batch.material, i, matPropBlock);
                        
                    }
                }
            }
        }
    }

    public class BakedTileImpostorDataSetGenerator
    {
        
        public struct ImpostorScatterSource
        {
            public PointCloudFromHoudiniAsset.PointCloudData[] pointCloudData;
            public float4x4 pointCloudTransform;
        }
        
        static readonly ProfilerMarker s_MarkerPrepare = new ProfilerMarker("BakedTileImpostorDataSetBaker.Prepare");
        static readonly ProfilerMarker s_MarkerRender = new ProfilerMarker("BakedTileImpostorDataSetBaker.RenderImpostor");

        public static string GetDirectoryPath(BakedTileImpostorDataSet dataset)
        {
            var path = AssetDatabase.GetAssetPath(dataset);
            if (path == null) return null;
            var name = Path.GetFileNameWithoutExtension(path);
            if (name == null) return null;
            var dirName = Path.GetDirectoryName(path);
            if (dirName == null) return null;
            return Path.Combine(dirName, $"impostorData_{name}/");
        }
        
        public void Generate(BakedTileImpostorDataSet dataset)
        {
            int2 proxyResolution = new int2(256, 256);
            int2 atlasMaxSize = new int2(2048, 2048);
            
            string directory = GetDirectoryPath(dataset);
            var path = AssetDatabase.GetAssetPath(dataset);
            var name = Path.GetFileNameWithoutExtension(path);
            string fileName = $"Impostor_{name}";
            
            if (dataset.ImpostorMeshDefinitions == null || dataset.ImpostorMeshDefinitions.Length == 0)
            {
                Debug.LogError("impostor mesh definitions not setup. Aborting");
                return;
            }
            
            s_MarkerPrepare.Begin();
            
            if (!Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var existingFiles = Directory.GetFiles(directory);
            foreach (var existingFile in existingFiles)
            {
                var fullPath = Path.GetFullPath(existingFile);
                if (File.Exists(fullPath))
                {
                    File.Delete(fullPath);
                }
                
            }
            
            EditorUtility.DisplayProgressBar("Scatter Impostor Baker", "Prepare Impostor bake data", 0.0f);

            List<PointCloudUtility.PointCloudDataEntry> dataList = new List<PointCloudUtility.PointCloudDataEntry>();
            int overallPointCount = 0;
            {
                foreach (var source in dataset.Sources)
                {
                    if (source.Asset == null) continue;
                    foreach (var pc in source.Asset.GetPointCloudData())
                    {
                        if (pc.positions == null || pc.positions.Length == 0 || pc.prefab == null)
                            continue;

                        dataList.Add(new PointCloudUtility.PointCloudDataEntry{transform = source.Transform, pcData = pc});
                        overallPointCount += pc.positions.Length;
                    }
                }
            }
            
            var dataArray = dataList.ToArray();
            PointCloudUtility.PointCloudPartitionOutput partitionOutput = PointCloudUtility.PartitionPointCloudDataToTiles(dataArray, dataset.TileSize, Allocator.TempJob);
            partitionOutput.pendingJobs.Complete();
            
            int2 cellCounts = partitionOutput.partitioningInfo.GetNumberOfCells();
            int cellCount = cellCounts.x * cellCounts.y;
            
            ImpostorScatteringRenderer.Batch[][] allBatches = new ImpostorScatteringRenderer.Batch[cellCount][];

            for (int cellIndex = 0; cellIndex < cellCount; ++cellIndex)
            {
                int indexMappingStartIndex = partitionOutput.offsetPerCell[cellIndex];
                int entryCount = partitionOutput.numberOfEntriesPerCell[cellIndex];
                if (dataset.UseAreaLimit)
                {
                    var cellBounds = partitionOutput.partitioningInfo.GetCellBounds(cellIndex);
                    allBatches[cellIndex] = null;
                    if (!dataset.AreaLimit.Contains(cellBounds.Center)) continue;
                }
                ImpostorScatteringRenderer.Batch[] batches = CreateBatches(cellIndex, partitionOutput.cellToIndexMapping.GetSubArray(indexMappingStartIndex, entryCount), partitionOutput.toPointCloudDataEntryMapping, dataArray, true);
                allBatches[cellIndex] = batches;
            }
            
            float originalVisibilityDistance = Shader.GetGlobalFloat(TileImpostorSystem.TileImpostorVisibilityDistanceName);
            float originalVisibilityDistanceFade = Shader.GetGlobalFloat(TileImpostorSystem.TileImpostorVisibilityDistanceFadeName);
            
            Shader.SetGlobalFloat(TileImpostorSystem.TileImpostorVisibilityDistanceName, 99999.0f);
            Shader.SetGlobalFloat(TileImpostorSystem.TileImpostorVisibilityDistanceFadeName, 0);
            
            BakedTileImpostorDataSet.TileImpostorType type = dataset.ImpostorType;
            OctaImpostorGenerator octaImpostorGenerator = null;
            TileMeshImpostorGenerator tileMeshGenerator = null;
            if (type == BakedTileImpostorDataSet.TileImpostorType.Octahedral)
            {
                octaImpostorGenerator = new OctaImpostorGenerator();
                octaImpostorGenerator.Init();
            } else
            {
                tileMeshGenerator = new TileMeshImpostorGenerator();
                tileMeshGenerator.Init(allBatches, proxyResolution, atlasMaxSize, directory, dataset.name);
            }

            ImpostorScatteringRenderer scatterRenderer = new ImpostorScatteringRenderer();
            List<BakedTileImpostorDataSet.GeneratedImposter> generatedImpostors = new List<BakedTileImpostorDataSet.GeneratedImposter>();
            ScatterImpostorGeneratorUI ui = new ScatterImpostorGeneratorUI();
            
            ui.MaxCellCount = cellCount;

            scatterRenderer.SetBoundsExtraMargin(dataset.ExtraBoundsMargin);
            
            s_MarkerPrepare.End();
            
            
            
            uint texelsToDilate = math.max((uint)math.ceil(40.0f * (float)dataset.FrameResolution / dataset.TileSize), 2u);
            for (int cellIndex = 0; cellIndex < cellCount; ++cellIndex)
            {
                ui.CurrentCellIndex = cellIndex;

                if (dataset.UseAreaLimit)
                {
                    var cellBounds = partitionOutput.partitioningInfo.GetCellBounds(cellIndex);
                    if (!dataset.AreaLimit.Contains(cellBounds.Center)) continue;
                }

                if (type != BakedTileImpostorDataSet.TileImpostorType.Octahedral)
                {
                    string msg = $"Render Scatter Impostors: Cell {cellIndex}/{cellCount})";
                    
                    float cellGenRatio = (float)cellIndex / cellCount;
                    EditorUtility.DisplayProgressBar("TileImpostorGenerator", msg, cellGenRatio);
                }
                
                int entryCount = partitionOutput.numberOfEntriesPerCell[cellIndex];

                if (entryCount == 0) continue;

                bool generateSpecSmoothness = false;
                uint2 frameResolution = new uint2((uint)dataset.FrameResolution, (uint)dataset.FrameResolution);
                using (s_MarkerRender.Auto())
                {
                    scatterRenderer.Clear();
                    scatterRenderer.AddBatches(allBatches[cellIndex]);

                    string outputPath = null;
                    switch (type)
                    {
                        case BakedTileImpostorDataSet.TileImpostorType.Octahedral:
                            outputPath = GenerateOctaImpostorForTile(octaImpostorGenerator, directory, fileName + $"_cell{cellIndex}",dataset.DiscardTileImpostorRatio, 
                                (uint2)dataset.FrameCount, frameResolution, generateSpecSmoothness, true, scatterRenderer, ui);
                            break;
                        case BakedTileImpostorDataSet.TileImpostorType.TileMesh:
                        case BakedTileImpostorDataSet.TileImpostorType.TileCards:
                            outputPath = GenerateTileMeshImpostorForTile(tileMeshGenerator, cellIndex, directory, fileName + $"_cell{cellIndex}",dataset.DiscardTileImpostorRatio, 
                                dataset.ImpostorMeshDefinitions, texelsToDilate, frameResolution, (uint)dataset.DownSampleCount, generateSpecSmoothness, scatterRenderer, dataset.HeightBias,
                                dataset.OverrideDiffusionProfile);
                            break;
                        default:
                            throw new ArgumentOutOfRangeException(nameof(type), type, null);
                    }

                    if (outputPath == null) continue;
                    
                    var prefab = PrefabUtility.LoadPrefabContents(outputPath);
                    var savedPrefab = PrefabUtility.SaveAsPrefabAsset(prefab, outputPath);
                    
                    generatedImpostors.Add(new BakedTileImpostorDataSet.GeneratedImposter()
                    {
                        ImpostorBounds = scatterRenderer.GetBounds(),
                        FlatTileIndex = cellIndex,
                        Prefab = savedPrefab,
                    });
                    
                    PrefabUtility.UnloadPrefabContents(prefab);
                    
                    
                }
            }
            
            EditorUtility.ClearProgressBar();

            dataset.PartitioningInfo = partitionOutput.partitioningInfo;
            dataset.GeneratedImposters = generatedImpostors.ToArray();
            
            Shader.SetGlobalFloat(TileImpostorSystem.TileImpostorVisibilityDistanceName, originalVisibilityDistance);
            Shader.SetGlobalFloat(TileImpostorSystem.TileImpostorVisibilityDistanceFadeName, originalVisibilityDistanceFade);
            
            Undo.ClearUndo(dataset);
            EditorUtility.SetDirty(dataset);
            AssetDatabase.SaveAssetIfDirty(dataset);
            
            partitionOutput.Dispose();

            octaImpostorGenerator?.Deinit();
            tileMeshGenerator?.Deinit();
            
        }

        ImpostorScatteringRenderer.PrefabEntry ExtractScatterImpostorPrefab(GameObject prefab)
        {

            if (prefab.TryGetComponent(out LODGroup lodGroup))
            {
                var lod = lodGroup.GetLODs()[0];
                foreach (var renderer in lod.renderers)
                {
                    if (renderer.TryGetComponent(out MeshFilter mf))
                    {
                        if (mf.sharedMesh != null && renderer.sharedMaterial != null)
                        {
                            Mesh mesh = mf.sharedMesh;
                            Material material = renderer.sharedMaterial;

                            return new ImpostorScatteringRenderer.PrefabEntry()
                            {
                                mesh = mesh,
                                material = material,
                                rootTransform = Matrix4x4.identity
                            };
                        }
                    }
                }
            }
            else
            {
                MeshRenderer[] meshRenderers = prefab.GetComponentsInChildren<MeshRenderer>(false);
                Matrix4x4 rootTransform = prefab.transform.worldToLocalMatrix;
                foreach (var mr in meshRenderers)
                {
                    Material mat = mr.sharedMaterial;
                    Mesh mesh = null;
                    if (mr.TryGetComponent(out MeshFilter mf))
                    {
                        mesh = mf.sharedMesh;
                    }

                    if (mesh != null && mat != null)
                    {

                        return new ImpostorScatteringRenderer.PrefabEntry()
                        {
                            mesh = mesh,
                            material = mat,
                            rootTransform = rootTransform
                        };
                    }
                }
            }

            return new ImpostorScatteringRenderer.PrefabEntry()
            {
                mesh = null,
                material = null,
                rootTransform = float4x4.identity
            }; ;
        }


        ImpostorScatteringRenderer.Batch[] CreateBatches(int cellIndex, NativeArray<int> indices, NativeArray<int2> scatterPointIndexToPointCloudDataMapping, PointCloudUtility.PointCloudDataEntry[] pointCloudDataList, bool applyRandomRotation)
        {
            if (indices.Length == 0) return null;

            Random rand = new Random((uint)cellIndex + 1);
            List<ImpostorScatteringRenderer.Batch> batches = new List<ImpostorScatteringRenderer.Batch>();
            int currentIndex = 0;
            int currentPointCloudIndex = scatterPointIndexToPointCloudDataMapping[indices[currentIndex]].x;
            List<Matrix4x4> batchTransforms = new List<Matrix4x4>();
            List<ImpostorScatteringRenderer.PerInstanceData> perInstanceData = new List<ImpostorScatteringRenderer.PerInstanceData>();
            while (currentIndex < indices.Length)
            {
                int2 pointCloudDataMapping = scatterPointIndexToPointCloudDataMapping[indices[currentIndex]];
                
                bool needToFlushBatch = pointCloudDataMapping.x != currentPointCloudIndex;

                if (needToFlushBatch)
                {
                    var prefab = pointCloudDataList[currentPointCloudIndex].pcData.prefab;
                    var prefabEntry = ExtractScatterImpostorPrefab(prefab);

                    if (prefabEntry.mesh != null && prefabEntry.material != null)
                    {
                        batches.Add(new ImpostorScatteringRenderer.Batch()
                        {
                            mesh = prefabEntry.mesh,
                            material = prefabEntry.material,
                            transforms = batchTransforms.ToArray(),
                            extraData = perInstanceData.ToArray(),
                            omitFromProxyAtlas = prefab.TryGetComponent<OmitFromTileImpostorProxyAtlas>(out _)
                        });
                    }
                    
                    batchTransforms.Clear();
                    perInstanceData.Clear();
                    currentPointCloudIndex = pointCloudDataMapping.x;
                }

                PointCloudFromHoudiniAsset.PointCloudData data = pointCloudDataList[currentPointCloudIndex].pcData;
                float4x4 parentTransform = pointCloudDataList[currentPointCloudIndex].transform;
                int dataIndex = pointCloudDataMapping.y;
                
                float3 pos = data.positions[dataIndex];
                float4 rotation = data.rotations[dataIndex];
                float scale = data.scales[dataIndex];

                quaternion newRotation;
                if (applyRandomRotation)
                {
                    quaternion randomRotation = math.mul(quaternion.AxisAngle(new float3(0.0f, 1.0f, 0.0f), rand.NextFloat() * math.PI2), quaternion.AxisAngle(new float3(1.0f, 0.0f, 0.0f), rand.NextFloat() * math.PIHALF));
                    newRotation = math.mul(new quaternion(rotation), randomRotation);
                }
                else
                {
                    newRotation = new quaternion(rotation);
                } 
                
                float4x4 localTransform = float4x4.TRS(pos, newRotation, new float3(scale, scale, scale));
                
                float4x4 transform = math.mul(parentTransform, localTransform);
                float age = data.age != null ? data.age[dataIndex] : 0;
                float health = data.health != null ? data.health[dataIndex] : 0;
                Color32 col = data.color != null ? data.color[dataIndex] : default;
                int colUint = col.r | col.g << 8 | col.b << 16 | col.a << 24;
                ImpostorScatteringRenderer.PerInstanceData perInstance = new ImpostorScatteringRenderer.PerInstanceData()
                {
                    _ScatteredInstanceExtraData = new float2(age, health)
                };

                perInstanceData.Add(perInstance);
                batchTransforms.Add(transform);
                ++currentIndex;
            }

            {
                var prefabEntry = ExtractScatterImpostorPrefab(pointCloudDataList[currentPointCloudIndex].pcData.prefab);

                if (prefabEntry.mesh != null && prefabEntry.material != null)
                {
                    batches.Add(new ImpostorScatteringRenderer.Batch()
                    {
                        mesh = prefabEntry.mesh,
                        material = prefabEntry.material,
                        transforms = batchTransforms.ToArray(),
                        extraData = perInstanceData.ToArray()
                    });
                }
            }

            return batches.ToArray();
        }

        string GenerateTileMeshImpostorForTile(TileMeshImpostorGenerator generator, int cellIndex, string directory, string fileName, float discardTileImpostorRatio, BakedTileImpostorDataSet.ImpostorMeshDefinition[] meshDefinitions,
            uint numberOfTexelsToDilate, uint2 renderResolution, uint downSampleOutput, bool generateSpecSmoothness, ImpostorScatteringRenderer renderer, float heightBias,
            DiffusionProfileSettings overrideDiffusionProfile = null)
        {
            TileMeshImpostorGenerator.GenerateImpostorSettings genSettings;
            genSettings.outputResolution = renderResolution;
            genSettings.downSampleOutput = downSampleOutput;
            genSettings.produceSpecularSmoothness = generateSpecSmoothness;
            genSettings.textureDilationAmount = (int)numberOfTexelsToDilate;
            genSettings.alphaDistanceSearchInTexels = (int)numberOfTexelsToDilate;
            genSettings.cameraDistance = 100.0f;
            genSettings.directoryPath = directory;
            genSettings.impostorName = fileName;
            genSettings.cellIndex = cellIndex;
            genSettings.renderer = renderer;
            genSettings.position = renderer.GetBounds().Center;
            genSettings.rotation = Quaternion.identity;
            genSettings.scale = new float3(1.0f, 1.0f, 1.0f);
            genSettings.overrideShader = null;
            genSettings.positionMeshToBoundsCenter = true;
            genSettings.impostorDiscardRatio = discardTileImpostorRatio;
            genSettings.overrideDiffusionProfile = overrideDiffusionProfile;
            genSettings.meshDefinitions = meshDefinitions;
            genSettings.impostorHeightBias = heightBias;
            var outputPath = generator.GenerateImpostor(genSettings);
            return outputPath;
        }

        string GenerateOctaImpostorForTile(OctaImpostorGenerator generator,string directory, string fileName, float discardTileImpostorRatio, 
            uint2 frameCount, uint2 frameResolution, bool generateSpecSmoothness, bool hemiOcta, ImpostorScatteringRenderer renderer, ScatterImpostorGeneratorUI ui)
        {
            OctaImpostorGenerator.GenerateImpostorSettings genSettings;
            genSettings.frameCount = frameCount;
            genSettings.frameResolution = frameResolution;
            genSettings.produceSpecularSmoothness = generateSpecSmoothness;
            genSettings.useHemiOcta = hemiOcta;
            genSettings.writeTexturesAsPNG = false;
            genSettings.cameraDistance = 100.0f;
            genSettings.alphaBlurRatio = 0.05f;
            genSettings.directoryPath = directory;
            genSettings.impostorName = fileName;
            genSettings.renderer = renderer;
            genSettings.position = renderer.GetBounds().Center;
            genSettings.rotation = Quaternion.identity;
            genSettings.scale = new float3(1.0f, 1.0f, 1.0f);
            genSettings.prefabToClone = null;
            genSettings.overrideShader = Resources.Load<Shader>("ImpostorRuntime/OctahedralImpostorFoliage");
            genSettings.applyBoundsOffsetToMesh = false;
            genSettings.customPostProcessAssetsCB = PostProcessAssets;
            genSettings.generatorListener = ui;
            genSettings.meshType = OctaImpostorGenerator.ImpostorMeshType.Octa;
            genSettings.customTextureDefinition = null;
            
            var outputPath = generator.GenerateImpostor(genSettings);
            return outputPath;
        }

        void PostProcessAssets(GameObject go, Material mat, Mesh mesh, IImpostorContext.ImpostorTexture[] textures)
        {
            mat.SetMaterialType(MaterialId.LitTranslucent);
            
        }

        
    }
}