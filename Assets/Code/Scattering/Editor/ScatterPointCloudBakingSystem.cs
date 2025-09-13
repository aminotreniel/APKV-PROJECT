using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading;
using Unity.Assertions;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;
using Unity.Entities.Serialization;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using Hash128 = Unity.Entities.Hash128;

namespace TimeGhost
{
    [WorldSystemFilter(WorldSystemFilterFlags.BakingSystem)]
    internal partial struct ScatterPointCloudBakingSystem : ISystem
    {
        [BakingType]
        internal struct RequiresSpatialPartitioningTag : IComponentData
        {
        }

        [BakingType]
        internal struct CalculatePointCloudPointImportanceTag : IComponentData
        {
        }

        [BakingType]
        internal struct AssignSceneSectionTag : IComponentData
        {
        }

        [BakingType]
        internal struct PointCloudPrefabSize : IComponentData
        {
            public AABB PrefabSize;
        }

        [BakingType]
        internal struct AuthoringBatchReference : ISharedComponentData
        {
            public Entity MainEntity;
        }

        [BakingType]
        internal struct PointCloudGroupActivationDistances : IComponentData
        {
            public float ActiveAreaMin;
            public float ActiveAreaMax;
        }

        struct PartitioningJobResults
        {
            public BlobBuilder InstanceDataBuilder;
            public BlobBuilder InstanceRangesBuilder;
            public BlobBuilder InstanceSizeMinMaxBuilder;
        }

        struct TileImportanceScratchData : IDisposable
        {
            public NativeArray<SortPointEntry> sortArray;
            public NativeArray<float> importanceData;
            public NativeReference<int3> tileDensityMinMaxAverage;

            public void Init(int pointCount, Allocator alloc)
            {
                sortArray = new NativeArray<SortPointEntry>(pointCount, alloc);
                importanceData = new NativeArray<float>(pointCount, alloc);
                tileDensityMinMaxAverage = new NativeReference<int3>(alloc);
            }


            public void Dispose()
            {
                if (sortArray.IsCreated)
                {
                    sortArray.Dispose();
                }

                if (importanceData.IsCreated)
                {
                    importanceData.Dispose();
                }

                if (tileDensityMinMaxAverage.IsCreated)
                {
                    tileDensityMinMaxAverage.Dispose();
                }
            }
        }

        struct SortPointEntry
        {
            public int index;
            public float weight;
            
            public struct SortPointComparer : IComparer<SortPointEntry>
            {

                // Compares by Length, Height, and Width.
                public int Compare(SortPointEntry a, SortPointEntry b)
                {

                    if (a.weight < b.weight)
                    {
                        return -1;
                    }

                    if (b.weight < a.weight)
                    {
                        return 1;
                    }

                    return 0;
                }
            }

        }

        [BurstCompile]
        private unsafe struct CalculateEntriesPerCell : IJobFor
        {
            [NativeDisableUnsafePtrRestriction] public float3* Points;
            [NativeDisableUnsafePtrRestriction] public int* NumberOfEntriesPerCell;
            public ScatterPointCloudSystem.PartitioningInfo PartitioningInfo;
            public int PointsPerThread;
            public int PointsCount;

            public void Execute(int index)
            {
                int from = PointsPerThread * index;
                int to = math.min(PointsPerThread * (index + 1), PointsCount);


                for (int i = from; i < to; ++i)
                {
                    int cellIndex = PartitioningInfo.GetFlatCellIndex(Points[i]);
                    Interlocked.Add(ref NumberOfEntriesPerCell[cellIndex], 1);
                }
            }
        }

        //lazy prefix sum todo: parallelize
        [BurstCompile]
        private struct PrefixSumEntriesPerCell : IJob
        {
            public NativeList<int> NumberOfEntriesPerCellPrefixSum;

            public void Execute()
            {
                int sum = 0;
                for (int i = 0; i < NumberOfEntriesPerCellPrefixSum.Length; ++i)
                {
                    int val = NumberOfEntriesPerCellPrefixSum[i];
                    NumberOfEntriesPerCellPrefixSum[i] = sum;
                    sum += val;
                }
            }
        }

        [BurstCompile]
        private unsafe struct FillPartitionedInstanceData : IJobFor
        {
            public BlobAssetReference<ScatterPointCloudInstanceAttributes> SourceAttributes;
            [NativeDisableUnsafePtrRestriction] public float3* PositionsDst;
            [NativeDisableUnsafePtrRestriction] public quaternion* OrientationsDst;
            [NativeDisableUnsafePtrRestriction] public float* ScalesDst;
            [NativeDisableUnsafePtrRestriction] public int* ExtraDataIndexDst;
            [NativeDisableParallelForRestriction] public NativeList<int> PerCellOffsets;
            public ScatterPointCloudSystem.PartitioningInfo PartitioningInfo;
            public int PointsPerThread;

            public void Execute(int index)
            {
                int from = PointsPerThread * index;
                int to = math.min(PointsPerThread * (index + 1), SourceAttributes.Value.Positions.Length);

                for (int i = from; i < to; ++i)
                {
                    int cellIndex = PartitioningInfo.GetFlatCellIndex(SourceAttributes.Value.Positions[i]);
                    int dataIndex = Interlocked.Add(ref PerCellOffsets.ElementAt(cellIndex), 1) - 1;

                    PositionsDst[dataIndex] = SourceAttributes.Value.Positions[i];
                    OrientationsDst[dataIndex] = SourceAttributes.Value.Orientations[i];
                    ScalesDst[dataIndex] = SourceAttributes.Value.Scales[i];
                    ExtraDataIndexDst[dataIndex] = i;
                }
            }
        }

        [BurstCompile]
        private unsafe struct FillPerCellOffsetAndCount : IJobFor
        {
            [ReadOnly] public NativeList<int>
                OffsetPerCell; //offset + count of a cell (or in other words, each entry contains offset to next cell)

            [NativeDisableUnsafePtrRestriction] public int2* AttributeRanges;

            public void Execute(int index)
            {
                int currentCellOffset = index == 0 ? 0 : OffsetPerCell[index - 1];
                int nextCellOffset = OffsetPerCell[index];
                int numberOfEntries = nextCellOffset - currentCellOffset;
                int offset = currentCellOffset;


                AttributeRanges[index] = new int2(offset, numberOfEntries);
            }
        }

        [BurstCompile]
        private unsafe struct CalculatePerCellMinMaxScale : IJobFor
        {
            public float ObjectSize;

            [NativeDisableUnsafePtrRestriction] [ReadOnly]
            public int2* AttributeRanges;

            [NativeDisableUnsafePtrRestriction] [ReadOnly]
            public float* Scales;

            [NativeDisableUnsafePtrRestriction] public float2* ScaleMinMax;

            public void Execute(int index)
            {
                float2 minMaxScale = new float2(float.MaxValue, 0);
                int2 offsetCount = AttributeRanges[index];
                for (int i = 0; i < offsetCount.y; ++i)
                {
                    int ind = offsetCount.x + i;
                    float scale = Scales[ind];

                    minMaxScale.x = math.min(minMaxScale.x, scale);
                    minMaxScale.y = math.max(minMaxScale.y, scale);
                }

                ScaleMinMax[index] = new float2(minMaxScale.x * ObjectSize, minMaxScale.y * ObjectSize);
            }
        }
        
        
        [BurstCompile]
        private struct CalculatePerPointImportanceForTile : IJob
        {
            public ScatterPointCloudSystem.PartitioningInfo DensityPartitioningInfo;
            [ReadOnly][NativeDisableContainerSafetyRestriction]
            public NativeList<int> PointsPerDensityCell;
            public ScatterPointCloudInstanceData SourceAttributes;
            public int RangeIndex;
            public TileImportanceScratchData Scratch;

            public int SampleDensityForPosition(float3 p)
            {
                float3 offsetPos = p;
                offsetPos.xz -= DensityPartitioningInfo.cellSize * 0.5f;
                int2 cellIndexMin = DensityPartitioningInfo.GetCellIndex(offsetPos);
                int2 cellIndexMax = DensityPartitioningInfo.GetCellIndex(offsetPos + DensityPartitioningInfo.cellSize);

                int i0 = DensityPartitioningInfo.FlattenCellIndex(new int2(cellIndexMin.x, cellIndexMin.y));
                int i1 = DensityPartitioningInfo.FlattenCellIndex(new int2(cellIndexMax.x, cellIndexMin.y));
                int i2 = DensityPartitioningInfo.FlattenCellIndex(new int2(cellIndexMin.x, cellIndexMax.y));
                int i3 = DensityPartitioningInfo.FlattenCellIndex(new int2(cellIndexMax.x, cellIndexMax.y));

                int s0 = PointsPerDensityCell[i0];
                int s1 = PointsPerDensityCell[i1];
                int s2 = PointsPerDensityCell[i2];
                int s3 = PointsPerDensityCell[i3];
                
                float2 frac = (offsetPos.xz - DensityPartitioningInfo.GetCellCenter(cellIndexMin)) / DensityPartitioningInfo.cellSize;

                float interp0 = s0 * (1.0f - frac.x) + s1 * frac.x;
                float interp1 = s2 * (1.0f - frac.x) + s3 * frac.x;
                return Mathf.RoundToInt(interp0 * (1.0f - frac.y) + interp1 * frac.y);

            }
            
            public void Execute()
            {
                bool hasValidRanges = SourceAttributes.AttributeRanges.IsCreated;
                int2 offsetCount = hasValidRanges ? SourceAttributes.AttributeRanges.Value.OffsetCount[RangeIndex] : new int2(0, SourceAttributes.Attributes.Value.Positions.Length);
                int baseOffset = offsetCount.x;
                int pointCount = offsetCount.y;

                if (pointCount == 0) return;
                
                SortPointEntry.SortPointComparer comparer = new SortPointEntry.SortPointComparer();

                Debug.Assert(offsetCount.y == Scratch.sortArray.Length);
                
                //produce density based importance
                {
                    ScatterPointCloudSystem.PartitioningInfo pointCloudPartitioningInfo;
                    pointCloudPartitioningInfo.cellSize = SourceAttributes.CellSizeInMeters;
                    pointCloudPartitioningInfo.bounds = SourceAttributes.PointCloudBounds;

                    AABB cellBounds = pointCloudPartitioningInfo.GetCellBounds(RangeIndex);
                    int2 densityTileMin = DensityPartitioningInfo.GetCellIndex(cellBounds.Min - DensityPartitioningInfo.cellSize);
                    int2 densityTileMax = DensityPartitioningInfo.GetCellIndex(cellBounds.Max + DensityPartitioningInfo.cellSize);
                
                    int3 tileDensityMinMaxSum = new int3(int.MaxValue, int.MinValue, 0);

                    for (int x = densityTileMin.x; x < densityTileMax.x; ++x)
                    {
                        for (int y = densityTileMin.y; y < densityTileMax.y; ++y)
                        {
                            int flatIndex = DensityPartitioningInfo.FlattenCellIndex(new int2(x, y));
                            int pointCountInCell = PointsPerDensityCell[flatIndex];

                            tileDensityMinMaxSum.x = math.min(tileDensityMinMaxSum.x, pointCountInCell);
                            tileDensityMinMaxSum.y = math.max(tileDensityMinMaxSum.y, pointCountInCell);
                            tileDensityMinMaxSum.z += pointCountInCell;
                        }
                    }

                    int densityCellCount = (densityTileMax.x - densityTileMin.x) * (densityTileMax.y - densityTileMin.y);
                    int densityAverage = Mathf.RoundToInt((float)tileDensityMinMaxSum.z / densityCellCount);

                    Scratch.tileDensityMinMaxAverage.Value = new int3(tileDensityMinMaxSum.x, tileDensityMinMaxSum.y, densityAverage);
                    
                    for (int i = 0; i < pointCount; ++i)
                    {
                        float3 pos = SourceAttributes.Attributes.Value.Positions[baseOffset + i];
                        float density = SampleDensityForPosition(pos);
                        
                        Scratch.sortArray[i] = new SortPointEntry()
                        {
                            index = i,
                            weight = density
                        };
                    }
                    
                    //sort based on density
                    Scratch.sortArray.Sort(comparer);
                    
                    //write scale based importance
                    for (int i = 0; i < pointCount; ++i)
                    {
                        float densityImp = (float)i / (pointCount - 1);
                        var targetIndex = Scratch.sortArray[i].index;
                        Scratch.importanceData[targetIndex] = densityImp;
                    }
                }
            }
        }

        private EntityQuery m_payloadsToSort;
        private EntityQuery m_calculatePointImportanceQuery;
        private EntityQuery m_sectionEntityQuery;
        private EntityQuery m_payloadsToAssignSceneSectionQuery;

        public void OnCreate(ref SystemState state)
        {
            m_payloadsToSort = state.EntityManager.CreateEntityQuery(
                ComponentType.ReadWrite<ScatterPointCloudInstanceData>(),
                ComponentType.ReadOnly<RequiresSpatialPartitioningTag>());

            m_calculatePointImportanceQuery = state.EntityManager.CreateEntityQuery(
                ComponentType.ReadWrite<ScatterPointCloudInstanceData>(),
                ComponentType.ReadOnly<CalculatePointCloudPointImportanceTag>());

            m_sectionEntityQuery = state.EntityManager.CreateEntityQuery(typeof(SectionMetadataSetup));

            m_payloadsToAssignSceneSectionQuery = state.EntityManager.CreateEntityQuery(
                ComponentType.ReadOnly<ScatterPointCloudInstanceData>(),
                ComponentType.ReadOnly<AuthoringBatchReference>(),
                ComponentType.ReadOnly<AssignSceneSectionTag>(),
                ComponentType.ReadWrite<SceneSection>());
        }

        public void OnDestroy(ref SystemState state)
        {
            m_payloadsToSort.Dispose();
            m_calculatePointImportanceQuery.Dispose();
            m_sectionEntityQuery.Dispose();
            m_payloadsToAssignSceneSectionQuery.Dispose();
        }

        private void AssignSceneSections(ref SystemState state)
        {
            //gather sections originating from different scatterers
            int runningIndex = 1;
            using var entitiesToAssignSceneSections = m_payloadsToAssignSceneSectionQuery.ToEntityArray(Allocator.TempJob);
            NativeHashMap<ValueTuple<Entity, int>, int> entityToIndexMapping = new NativeHashMap<ValueTuple<Entity, int>, int>(64, Allocator.Temp);
            foreach (var ent in entitiesToAssignSceneSections)
            {
                if (!state.EntityManager.HasComponent<SceneSection>(ent)) continue;
                var comp = state.EntityManager.GetSharedComponent<SceneSection>(ent);
                var mainEntity = state.EntityManager.GetSharedComponent<AuthoringBatchReference>(ent).MainEntity;

                var key = new ValueTuple<Entity, int>(mainEntity, comp.Section);
                if (!entityToIndexMapping.ContainsKey(key))
                {
                    entityToIndexMapping[key] = runningIndex++;
                }
            }

            foreach (var ent in entitiesToAssignSceneSections)
            {
                if (!state.EntityManager.HasComponent<SceneSection>(ent)) continue;
                var mainEntity = state.EntityManager.GetSharedComponent<AuthoringBatchReference>(ent).MainEntity;

                var comp = state.EntityManager.GetSharedComponent<SceneSection>(ent);
                var key = new ValueTuple<Entity, int>(mainEntity, comp.Section);

                if (!entityToIndexMapping.TryGetValue(key, out var sectionIndex))
                {
                    Debug.LogError("Failed to map a scene section to index, some scatter tiles are going to be invalid!");
                    continue;
                }

                comp.Section = sectionIndex;
                state.EntityManager.SetSharedComponent(ent, comp);
            }

            entityToIndexMapping.Dispose();
        }

        private void PartitionPayloadsToTiles(ref SystemState state)
        {
            var pointCloudsToPartition =
                m_payloadsToSort.ToComponentDataArray<ScatterPointCloudInstanceData>(Allocator.TempJob);
            var pointCloudsToPartitionEntities = m_payloadsToSort.ToEntityArray(Allocator.TempJob);

            state.EntityManager.RemoveComponent<RequiresSpatialPartitioningTag>(m_payloadsToSort);

            NativeList<PartitioningJobResults> jobResults =
                new NativeList<PartitioningJobResults>(pointCloudsToPartition.Length, Allocator.Temp);

            JobHandle jobsHandle = default;
            for (int i = 0; i < pointCloudsToPartition.Length; ++i)
            {
                var instanceData = pointCloudsToPartition[i];
                var prefabSize = state.EntityManager.GetComponentData<PointCloudPrefabSize>(pointCloudsToPartitionEntities[i]);
                var prefabSizeFloat = CalculateApproximatePrefabSize(prefabSize.PrefabSize);
                var h = ScheduleJobsForInstanceData(ref instanceData, prefabSizeFloat, state.Dependency, out PartitioningJobResults results);
                jobResults.Add(results);


                jobsHandle = JobHandle.CombineDependencies(jobsHandle, h);
            }

            jobsHandle.Complete();

            for (int i = 0; i < pointCloudsToPartition.Length; ++i)
            {
                Entity ent = pointCloudsToPartitionEntities[i];
                PartitioningJobResults partitioningJobResult = jobResults[i];
                ScatterPointCloudInstanceData instanceData = pointCloudsToPartition[i];
                SceneSection sceneSection = state.EntityManager.GetSharedComponent<SceneSection>(ent);

                if (instanceData.Attributes.IsCreated)
                {
                    instanceData.Attributes.Dispose();
                }

                if (instanceData.AttributeRanges.IsCreated)
                {
                    instanceData.AttributeRanges.Dispose();
                }

                instanceData.Attributes =
                    partitioningJobResult.InstanceDataBuilder.CreateBlobAssetReference<ScatterPointCloudInstanceAttributes>(
                        Allocator.Persistent);
                instanceData.AttributeRanges =
                    partitioningJobResult.InstanceRangesBuilder.CreateBlobAssetReference<ScatterPointCloudAttributeRange>(
                        Allocator.Persistent);
                instanceData.InstanceSizeMinMax =
                    partitioningJobResult.InstanceSizeMinMaxBuilder.CreateBlobAssetReference<ScatterPointCloudInstanceSizeMinMax>(
                        Allocator.Persistent);


                partitioningJobResult.InstanceDataBuilder.Dispose();
                partitioningJobResult.InstanceRangesBuilder.Dispose();
                partitioningJobResult.InstanceSizeMinMaxBuilder.Dispose();

                state.EntityManager.SetComponentData(ent, instanceData);

                //add bounds to the section data
                var sectionEntity = SerializeUtility.GetSceneSectionEntity(sceneSection.Section,
                    state.EntityManager, ref m_sectionEntityQuery, true);

                var mainEntity = state.EntityManager.GetSharedComponent<AuthoringBatchReference>(ent).MainEntity;
                var activationDistances = state.EntityManager.GetComponentData<PointCloudGroupActivationDistances>(mainEntity);

                if (state.EntityManager.HasComponent<ScatterSceneLoaderSystem.ScatterSceneSectionMetaData>(sectionEntity))
                {
                    var existingMetaData = state.EntityManager.GetComponentData<ScatterSceneLoaderSystem.ScatterSceneSectionMetaData>(sectionEntity);
                    existingMetaData.NumberOfInstances += instanceData.Attributes.Value.Positions.Length;
                    existingMetaData.ActiveAreaMin = math.min(existingMetaData.ActiveAreaMin, activationDistances.ActiveAreaMin);
                    existingMetaData.ActiveAreaMax = math.max(existingMetaData.ActiveAreaMax, activationDistances.ActiveAreaMax);

                    state.EntityManager.SetComponentData(sectionEntity, existingMetaData);
                }
                else
                {
                    state.EntityManager.AddComponentData(sectionEntity, new ScatterSceneLoaderSystem.ScatterSceneSectionMetaData() { Bounds = instanceData.PointCloudBounds, NumberOfInstances = instanceData.Attributes.Value.Positions.Length, ActiveAreaMin = activationDistances.ActiveAreaMin, ActiveAreaMax = activationDistances.ActiveAreaMax });
                }
            }

            pointCloudsToPartitionEntities.Dispose();
            pointCloudsToPartition.Dispose();

            jobResults.Dispose();
        }

        private void CalculatePerPointImportanceParams(ref SystemState state)
        {
            const float densityCellSizeInMeters = 1;
            const int entriesPerThread = 64;

            MinMaxAABB fullBounds = MinMaxAABB.Empty;
            using var pointCloudsToParticipate =
                m_calculatePointImportanceQuery.ToComponentDataArray<ScatterPointCloudInstanceData>(Allocator.TempJob);
            using var pointCloudEntitites = m_calculatePointImportanceQuery.ToEntityArray(Allocator.TempJob);

            int totalTileCount = 0;
            for (int i = 0; i < pointCloudsToParticipate.Length; ++i)
            {
                ScatterPointCloudInstanceData instanceData = pointCloudsToParticipate[i];
                var b = instanceData.PointCloudBounds;
                fullBounds.Encapsulate(b);

                if (instanceData.AttributeRanges.IsCreated)
                {
                    totalTileCount += instanceData.AttributeRanges.Value.OffsetCount.Length;
                }
                else
                {
                    totalTileCount += 1;
                }
            }

            ScatterPointCloudSystem.PartitioningInfo partitionInfo;
            partitionInfo.bounds = fullBounds;
            partitionInfo.cellSize = densityCellSizeInMeters;

            int2 cellCounts = partitionInfo.GetNumberOfCells();
            int cellCount = cellCounts.x * cellCounts.y;

            NativeList<int> numberOfEntriesPerCell = new NativeList<int>(cellCount, Allocator.TempJob);
            numberOfEntriesPerCell.AddReplicate(0, cellCount);

            //calculate density of all points from the scene per meter
            JobHandle previousDeps = default;
            JobHandle combinedJobs = default;
            unsafe
            {
                for (int i = 0; i < pointCloudsToParticipate.Length; ++i)
                {
                    var data = pointCloudsToParticipate[i];
                    var pointCount = data.Attributes.Value.Positions.Length;

                    var numberOfBatches = (pointCount + entriesPerThread - 1) / entriesPerThread;

                    var jobHandle = new CalculateEntriesPerCell()
                    {
                        Points = (float3*)data.Attributes.Value.Positions.GetUnsafePtr(),
                        NumberOfEntriesPerCell = numberOfEntriesPerCell.GetUnsafePtr(),
                        PartitioningInfo = partitionInfo,
                        PointsPerThread = entriesPerThread,
                        PointsCount = pointCount
                    }.ScheduleParallel(numberOfBatches, 64, previousDeps);

                    combinedJobs = JobHandle.CombineDependencies(combinedJobs, jobHandle);
                }
            }

            previousDeps = combinedJobs;
            combinedJobs = default;

            //kick jobs to calculate per point importance in a tile (used to infer what points are "less important" and can be hidden earlier)
            NativeArray<TileImportanceScratchData> perTileScratch = new NativeArray<TileImportanceScratchData>(totalTileCount, Allocator.TempJob);
            {
                int scratchIndex = 0;
                for (int i = 0; i < pointCloudsToParticipate.Length; ++i)
                {
                    ScatterPointCloudInstanceData instanceData = pointCloudsToParticipate[i];

                    bool hasValidRanges = instanceData.AttributeRanges.IsCreated;
                    var tilesInPC = hasValidRanges ? instanceData.AttributeRanges.Value.OffsetCount.Length : 1;
                
                    for (int k = 0; k < tilesInPC; ++k)
                    {
                        var pointCount = hasValidRanges ? instanceData.AttributeRanges.Value.OffsetCount[k].y : instanceData.Attributes.Value.Positions.Length;
                        if (pointCount == 0) continue;


                        TileImportanceScratchData scratchData = new TileImportanceScratchData();
                        scratchData.Init(pointCount, Allocator.TempJob);
                        perTileScratch[scratchIndex] = scratchData;
                    
                        var jobHandle = new CalculatePerPointImportanceForTile()
                        {
                            DensityPartitioningInfo = partitionInfo,
                            PointsPerDensityCell = numberOfEntriesPerCell,
                            SourceAttributes = instanceData,
                            RangeIndex = k,
                            Scratch = perTileScratch[scratchIndex],
                        }.Schedule(previousDeps);

                    
                        combinedJobs = JobHandle.CombineDependencies(combinedJobs, jobHandle);
                        ++scratchIndex;
                    }
                }
            }
            combinedJobs.Complete();
            
            //write out importance data to a blob
            {
                int scratchIndex = 0;
                for (int i = 0; i < pointCloudsToParticipate.Length; ++i)
                {
                    ScatterPointCloudInstanceData instanceData = pointCloudsToParticipate[i];
                    bool hasValidRanges = instanceData.AttributeRanges.IsCreated;
                    var tilesInPC = hasValidRanges ? instanceData.AttributeRanges.Value.OffsetCount.Length : 1;
                    int pointCount = instanceData.Attributes.Value.Positions.Length;
                
                    var importanceDataBuilder = new BlobBuilder(Allocator.Temp);
                    ref ScatterPointCloudPointImportanceData importanceData = ref importanceDataBuilder.ConstructRoot<ScatterPointCloudPointImportanceData>();
                    BlobBuilderArray<float> perPointImportance = importanceDataBuilder.Allocate(ref importanceData.PerPointImportanceData, pointCount);
                    BlobBuilderArray<int3> perTileImportance = importanceDataBuilder.Allocate(ref importanceData.PerTileDensityMinMaxAverage, tilesInPC);
                    
                
                    for (int k = 0; k < tilesInPC; ++k)
                    {
                        int2 offsetCount = hasValidRanges ? instanceData.AttributeRanges.Value.OffsetCount[k] : new int2(0, instanceData.Attributes.Value.Positions.Length);
                        
                        var pointsInTile = offsetCount.y;
                        if (pointsInTile == 0)
                        {
                            perTileImportance[k] = new int3(0, 0, 0);
                            continue;
                        }

                        TileImportanceScratchData scratch = perTileScratch[scratchIndex];

                        perTileImportance[k] = scratch.tileDensityMinMaxAverage.Value;

                        for (int pIndex = 0; pIndex < pointsInTile; ++pIndex)
                        {
                            perPointImportance[offsetCount.x + pIndex] = scratch.importanceData[pIndex];
                        }
                        
                        scratch.Dispose();
                        ++scratchIndex;
                    }

                    instanceData.ImportanceData = importanceDataBuilder.CreateBlobAssetReference<ScatterPointCloudPointImportanceData>(Allocator.Persistent);
                    importanceDataBuilder.Dispose();
                    
                    state.EntityManager.SetComponentData(pointCloudEntitites[i], instanceData);
                }
            }
            

            state.EntityManager.RemoveComponent<CalculatePointCloudPointImportanceTag>(m_calculatePointImportanceQuery);
            numberOfEntriesPerCell.Dispose();
            perTileScratch.Dispose();
        }


            public void OnUpdate(ref SystemState state)
            {
                AssignSceneSections(ref state); //need to assign scene sections before partition so when partitioning assigns bounds to the section meta data, it has up-to-date section index
                PartitionPayloadsToTiles(ref state);
                CalculatePerPointImportanceParams(ref state);
            }

            float CalculateApproximatePrefabSize(AABB size)
            {
                return size.Size.x * 0.5f + size.Size.y + size.Size.z * 0.5f; //simple heuristic for approximate "size on screen", height matters more than width/depth
            }

            private JobHandle ScheduleJobsForInstanceData(ref ScatterPointCloudInstanceData data, float prefabSize, JobHandle jh,
                out PartitioningJobResults results)
            {
                const int ENTRIES_PER_THREAD = 64;

                ScatterPointCloudSystem.PartitioningInfo partitioningInfo;
                partitioningInfo.bounds = data.PointCloudBounds;
                partitioningInfo.cellSize = data.CellSizeInMeters;


                var pointCount = data.Attributes.Value.Positions.Length;

                var numberOfBatches = (pointCount + ENTRIES_PER_THREAD - 1) / ENTRIES_PER_THREAD;
                var entriesPerThread = ENTRIES_PER_THREAD;
                var cellCount = partitioningInfo.GetNumberOfCells().x * partitioningInfo.GetNumberOfCells().y;

                NativeList<int> numberOfEntriesPerCell = new NativeList<int>(cellCount, Allocator.TempJob);
                numberOfEntriesPerCell.AddReplicate(0, cellCount);

                //attributes builder
                var instanceDataBuilder = new BlobBuilder(Allocator.Temp);
                ref ScatterPointCloudInstanceAttributes attributes =
                    ref instanceDataBuilder.ConstructRoot<ScatterPointCloudInstanceAttributes>();
                BlobBuilderArray<float3> positionsBuilder =
                    instanceDataBuilder.Allocate(ref attributes.Positions, pointCount);
                BlobBuilderArray<quaternion> orientationsBuilder =
                    instanceDataBuilder.Allocate(ref attributes.Orientations, pointCount);
                BlobBuilderArray<float> scalesBuilder = instanceDataBuilder.Allocate(ref attributes.Scales, pointCount);
                BlobBuilderArray<int> extraDataIndexBuilder = instanceDataBuilder.Allocate(ref attributes.ExtraDataIndex, pointCount);

                //ranges builder
                var rangesBuilder = new BlobBuilder(Allocator.Temp);
                ref ScatterPointCloudAttributeRange attributeRanges =
                    ref rangesBuilder.ConstructRoot<ScatterPointCloudAttributeRange>();
                BlobBuilderArray<int2> rangesArray = rangesBuilder.Allocate(ref attributeRanges.OffsetCount, cellCount);

                //min max scale per cell builder
                var sizesBuilder = new BlobBuilder(Allocator.Temp);
                ref ScatterPointCloudInstanceSizeMinMax sizesBlob =
                    ref sizesBuilder.ConstructRoot<ScatterPointCloudInstanceSizeMinMax>();
                BlobBuilderArray<float2> sizesArray = sizesBuilder.Allocate(ref sizesBlob.SizeMinMax, cellCount);

                JobHandle jobHandle = default;
                unsafe
                {
                    jobHandle = new CalculateEntriesPerCell()
                    {
                        Points = (float3*)data.Attributes.Value.Positions.GetUnsafePtr(),
                        NumberOfEntriesPerCell = numberOfEntriesPerCell.GetUnsafePtr(),
                        PartitioningInfo = partitioningInfo,
                        PointsPerThread = entriesPerThread,
                        PointsCount = pointCount
                    }.Schedule(numberOfBatches, jh);

                    jobHandle = new PrefixSumEntriesPerCell()
                    {
                        NumberOfEntriesPerCellPrefixSum = numberOfEntriesPerCell
                    }.Schedule(jobHandle);


                    jobHandle = new FillPartitionedInstanceData()
                    {
                        ExtraDataIndexDst = (int*)extraDataIndexBuilder.GetUnsafePtr(),
                        OrientationsDst = (quaternion*)orientationsBuilder.GetUnsafePtr(),
                        PositionsDst = (float3*)positionsBuilder.GetUnsafePtr(),
                        ScalesDst = (float*)scalesBuilder.GetUnsafePtr(),
                        PartitioningInfo = partitioningInfo,
                        PerCellOffsets = numberOfEntriesPerCell,
                        PointsPerThread = entriesPerThread,
                        SourceAttributes = data.Attributes
                    }.Schedule(numberOfBatches, jobHandle);

                    jobHandle = new FillPerCellOffsetAndCount()
                    {
                        AttributeRanges = (int2*)rangesArray.GetUnsafePtr(),
                        OffsetPerCell = numberOfEntriesPerCell,
                    }.Schedule(cellCount, jobHandle);

                    jobHandle = new CalculatePerCellMinMaxScale()
                    {
                        ObjectSize = prefabSize,
                        AttributeRanges = (int2*)rangesArray.GetUnsafePtr(),
                        Scales = (float*)scalesBuilder.GetUnsafePtr(),
                        ScaleMinMax = (float2*)sizesArray.GetUnsafePtr()
                    }.Schedule(cellCount, jobHandle);
                }

                numberOfEntriesPerCell.Dispose(jobHandle);

                results.InstanceDataBuilder = instanceDataBuilder;
                results.InstanceRangesBuilder = rangesBuilder;
                results.InstanceSizeMinMaxBuilder = sizesBuilder;

                return jobHandle;
            }
        }
    }