using System.Collections;
using System.Collections.Generic;
using System.Threading;
using Unity.Burst;
using Unity.Burst.Intrinsics;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using Unity.Transforms;
using UnityEngine.Rendering;
using Random = UnityEngine.Random;

namespace TimeGhost
{
    public partial struct ScatteredInstanceSpatialPartitioningSystem : ISystem
    {
        #region BurstJobs
        [BurstCompile]
        private struct AddEntitiesToPartitioningJob : IJobChunk
        {
            public float2 WorldCorner;
            public uint2 GridResolution;
            public float CellSizeInv;
            [ReadOnly] 
            public ComponentTypeHandle<LocalToWorld> TransformType;
            public ComponentTypeHandle<ScatteredInstancePartitioningData> ScatteredPartitioningData;
            [ReadOnly] public BufferLookup<ScatteredInstanceChildren> ChildBufferLookup;
            [ReadOnly] public EntityTypeHandle EntityType;
            [NativeDisableParallelForRestriction]
            public ComponentLookup<ScatteredInstanceRenderTileData> RenderTileDataLookup;
            [NativeDisableContainerSafetyRestriction] 
            public NativeArray<int> InstanceCountsPerTile;
            [NativeDisableContainerSafetyRestriction][NativeDisableParallelForRestriction]
            public NativeArray<int> ChangesInTile;

            public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask)
            {
                var chunkTransforms = chunk.GetNativeArray(ref TransformType);
                var chunkScatteredPartioningData = chunk.GetNativeArray(ref ScatteredPartitioningData);
                
                var entities = chunk.GetNativeArray(EntityType);
                
                var enumerator = new ChunkEntityEnumerator(useEnabledMask,
                    chunkEnabledMask, chunk.Count);
                
                while(enumerator.NextEntityIndex(out int i))
                {
                    var transform = chunkTransforms[i];
                    var worldPos = transform.Position.xz;
                    var entity = entities[i];

                    if (math.isnan(worldPos.x) || math.isnan(worldPos.y))
                    {
                        ScatteredInstancePartitioningData partitioningComponent = new ScatteredInstancePartitioningData()
                        {
                            FlatTileIndex = -1,
                            IndexInTile = -1
                        };
                        chunkScatteredPartioningData[i] = partitioningComponent;
                    }
                    else
                    {
                        int tileIndex = CalculateFlatTileIndex(ref worldPos, ref WorldCorner, ref GridResolution, CellSizeInv);

                        int indexInTile = -1;
                        if (tileIndex != -1)
                        {
                            unsafe
                            {
                                indexInTile = Interlocked.Increment(ref ((int*)InstanceCountsPerTile.GetUnsafePtr())[tileIndex]) - 1;
                            }
                            ChangesInTile[tileIndex] = 1;
                        }

                        ScatteredInstancePartitioningData partitioningComponent = new ScatteredInstancePartitioningData()
                        {
                            FlatTileIndex = tileIndex,
                            IndexInTile = indexInTile
                        };
                        chunkScatteredPartioningData[i] = partitioningComponent;

                        //replicate tile Data to children (in case of lods)
                        ScatteredInstanceRenderTileData renderData = new ScatteredInstanceRenderTileData()
                            { TileIndices = new float2(tileIndex, indexInTile) };

                        if (RenderTileDataLookup.HasComponent(entity))
                        {
                            RenderTileDataLookup[entity] = renderData;
                        }

                        if (ChildBufferLookup.TryGetBuffer(entity, out var childBuffer))
                        {
                            for (int childIndex = 0; childIndex < childBuffer.Length; ++childIndex)
                            {
                                var child = childBuffer[childIndex].Child;
                                if (RenderTileDataLookup.HasComponent(child))
                                {
                                    RenderTileDataLookup[child] = renderData;
                                }
                                
                            }
                        }
                        
                    }
                }
            }
        }
        
        [BurstCompile]
        private struct GatherRemovedEntitiesPerTile : IJobChunk
        {
            [NativeDisableParallelForRestriction]
            public ComponentLookup<ScatteredInstancePartitioningData> PartitionedDataLookup;
            [NativeDisableParallelForRestriction]
            public EntityTypeHandle EntityTypeHandle;

            public NativeParallelMultiHashMap<int, Entity>.ParallelWriter RemovedEntries; 
            
            public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask)
            {
                var entityArray = chunk.GetNativeArray(EntityTypeHandle);

                var enumerator = new ChunkEntityEnumerator(useEnabledMask,
                    chunkEnabledMask, chunk.Count);

                while (enumerator.NextEntityIndex(out int i))
                {
                    var entityToRemove = entityArray[i];
                    if (!PartitionedDataLookup.TryGetComponent(entityToRemove, out var tileDataEntryToRemove)) continue;

                    if (tileDataEntryToRemove.IndexInTile == -1 || tileDataEntryToRemove.FlatTileIndex == -1) continue;
                    
                    RemovedEntries.Add(tileDataEntryToRemove.FlatTileIndex, entityToRemove);
                }
            }
        }
        [BurstCompile]
        private struct RemoveEntitiesFromPartitioningJob : IJobParallelFor
        {
            [ReadOnly]
            public NativeParallelMultiHashMap<int, Entity>.ReadOnly RemovedEntries;
            [NativeDisableContainerSafetyRestriction][NativeDisableParallelForRestriction]
            public NativeArray<int> InstanceCountsPerTile;
            [NativeDisableContainerSafetyRestriction][NativeDisableParallelForRestriction]
            public NativeArray<int> ChangesInTile;
            [NativeDisableUnsafePtrRestriction][NativeDisableParallelForRestriction]
            public UnsafeList<Entity> EntityHandlePerDataEntry;
            [NativeDisableUnsafePtrRestriction][NativeDisableParallelForRestriction]
            public UnsafeList<ScatteredInstancePropertiesPacked> InstanceDataPages;
            [NativeDisableUnsafePtrRestriction][NativeDisableParallelForRestriction]
            public NativeArray<UnsafeList<int>> PerTileInstanceData;
            [NativeDisableParallelForRestriction]
            public ComponentLookup<ScatteredInstancePartitioningData> PartitionedDataLookup;
            [NativeDisableParallelForRestriction]
            public BufferLookup<ScatteredInstanceChildren> InstanceChildrenLookup;
            [NativeDisableParallelForRestriction]
            public ComponentLookup<ScatteredInstanceRenderTileData> ChildTileDataLookup;
            
            public void Execute(int index)
            {
                if (!RemovedEntries.TryGetFirstValue(index, out var removedEntity, out var iterator))
                {
                    return;
                }

                //mark this tile as changed
                ChangesInTile[index] = 1;
                var tileDataPagesArray = PerTileInstanceData[index];
                
                int tileIndex = index;
                do
                {
                    
                    if (!PartitionedDataLookup.TryGetComponent(removedEntity, out var tileDataEntry))
                    {
                        continue;
                    }


                    int indexInTile = tileDataEntry.IndexInTile;
                    
                    if (InstanceCountsPerTile[tileIndex] > 0)
                    {
                        
                        var currentValue = InstanceCountsPerTile[tileIndex];
                        int tailIndex = currentValue - 1;
                        InstanceCountsPerTile[tileIndex] = currentValue - 1;
                        //swapback remove
                        if(tailIndex != indexInTile)
                        {
                            var indexToPageMapping = tailIndex / INSTANCE_DATA_PAGE_SIZE;
                            var pageIndex = tileDataPagesArray[indexToPageMapping];
                            var tailDataIndex = CalculateDataEntryIndex(pageIndex, tailIndex);

                            indexToPageMapping = indexInTile / INSTANCE_DATA_PAGE_SIZE;
                            pageIndex = tileDataPagesArray[indexToPageMapping];
                            var removedDataIndex = CalculateDataEntryIndex(pageIndex, indexInTile);

                            //swap data entries
                            InstanceDataPages[removedDataIndex] = InstanceDataPages[tailDataIndex];
                            var tailEntity = EntityHandlePerDataEntry[tailDataIndex];
                            EntityHandlePerDataEntry[removedDataIndex] = tailEntity;

                            //fixup component data
                            if (PartitionedDataLookup.HasComponent(tailEntity))
                            {
                                PartitionedDataLookup[tailEntity] = tileDataEntry;
                            }
                            
                            //propagate to children
                            //replicate tile Data to children
                            if (InstanceChildrenLookup.TryGetBuffer(tailEntity,
                                    out DynamicBuffer<ScatteredInstanceChildren> childrenBuff))
                            {
                                ScatteredInstanceRenderTileData renderData = new ScatteredInstanceRenderTileData()
                                    { TileIndices = new float2(tileIndex, indexInTile) };
                                for (int childIndex = 0; childIndex < childrenBuff.Length; ++childIndex)
                                {
                                    ChildTileDataLookup[childrenBuff[childIndex].Child] = renderData;
                                }
                            }
                        }
                            
                        
                    }
                } 
                while (RemovedEntries.TryGetNextValue(out removedEntity, ref iterator));
            }
        }

        [BurstCompile]
        private struct UpdateTileRevisionNumberJob : IJobParallelFor
        {
            [NativeDisableParallelForRestriction]
            public NativeArray<int> ChangesInTile;
            
            [NativeDisableParallelForRestriction]
            public NativeArray<int> TileRevision;
            
            public void Execute(int index)
            {
                if (ChangesInTile[index] != 0)
                {
                    TileRevision[index] = TileRevision[index] + 1;
                }
            }
        }
        [BurstCompile]
        private unsafe struct FreeUnusedPagesJob : IJobFor 
        {
            [ReadOnly] public NativeArray<int> InstancesPerTile;
            [NativeDisableParallelForRestriction]
            public NativeArray<UnsafeList<int>> PerTileInstancePages;
            [NativeDisableParallelForRestriction]
            public NativeArray<int> FreePages;
            [NativeDisableUnsafePtrRestriction] [NativeDisableParallelForRestriction]
            public int* FreePagesCounter; 
            
            public void Execute(int index)
            {
                int numberOfEntries = InstancesPerTile[index];
                int requiredPageCount = (numberOfEntries + INSTANCE_DATA_PAGE_SIZE - 1) / INSTANCE_DATA_PAGE_SIZE;
                var currentPageCount = PerTileInstancePages[index].IsCreated ? PerTileInstancePages[index].Length : 0;
                
                int pagesNeededChange = requiredPageCount - currentPageCount;
                
                if(pagesNeededChange < 0)
                {
                    var pagesArray =  PerTileInstancePages[index];
                    int pagesToRelease = -pagesNeededChange;
                    int firstPageIndex = Interlocked.Add(ref UnsafeUtility.AsRef<int>(FreePagesCounter), pagesToRelease) - pagesToRelease;
                    for (int i = 0; i < pagesToRelease; ++i)
                    {
                        int pageToFree = pagesArray[^(i + 1)];
                        FreePages[firstPageIndex + i] = pageToFree;
                    }
                    
                    pagesArray.Resize(pagesArray.Length - pagesToRelease);
                    PerTileInstancePages[index] = pagesArray;
                }
                
                
            }
        }
        
        [BurstCompile]
        private unsafe struct ResizePerTileArraysAndReservePagesJob : IJobFor 
        {
            [ReadOnly] public NativeArray<int> InstancesPerTile;
            [NativeDisableParallelForRestriction]
            public NativeArray<UnsafeList<int>> PerTileInstancePages;
            [NativeDisableUnsafePtrRestriction] [NativeDisableParallelForRestriction]
            public int* PagesCounter; 
            [NativeDisableParallelForRestriction]
            public NativeArray<int> FreePages;
            [NativeDisableUnsafePtrRestriction] [NativeDisableParallelForRestriction]
            public int* FreePagesCounter; 
            
            public void Execute(int index)
            {
                const int initialMinSize = 16;
                int numberOfEntries = InstancesPerTile[index];
                int requiredPageCount = (numberOfEntries + INSTANCE_DATA_PAGE_SIZE - 1) / INSTANCE_DATA_PAGE_SIZE;
                
                var currentPageCount = PerTileInstancePages[index].IsCreated ? PerTileInstancePages[index].Length : 0;
                
                int pagesNeededChange = requiredPageCount - currentPageCount;
                
                if (pagesNeededChange > 0)
                {
                    if (!PerTileInstancePages[index].IsCreated)
                    {
                        PerTileInstancePages[index] = new UnsafeList<int>(math.max(initialMinSize, requiredPageCount), Allocator.Persistent);
                    }

                    var pagesArray =  PerTileInstancePages[index];
                    int pagesStillNeeded = pagesNeededChange;
                    
                    //check if we can get pages from free list
                    if (*FreePagesCounter > 0)
                    {
                        int pagesToReserve = math.min(*FreePagesCounter, pagesStillNeeded);
                        if (pagesToReserve > 0)
                        {
                            var newValue = Interlocked.Add(ref UnsafeUtility.AsRef<int>(FreePagesCounter), -pagesToReserve);
                            if (newValue < 0) //went overboard. just reserve from new pages
                            {
                                Interlocked.Add(ref UnsafeUtility.AsRef<int>(FreePagesCounter), pagesToReserve);
                            }
                            else
                            {
                                for (int i = 0; i < pagesToReserve; ++i)
                                {
                                    int freePage =  FreePages[newValue + i];
                                    pagesArray.Add(freePage);
                                }

                                pagesStillNeeded -= pagesToReserve;
                            }
                        }
                    }

                    //free list exhausted, if we still need new pages, allocate more
                    if(pagesStillNeeded > 0)
                    {
                        int firstPageIndex = Interlocked.Add(ref UnsafeUtility.AsRef<int>(PagesCounter), pagesStillNeeded) - pagesStillNeeded;
                        for (int i = 0; i < pagesStillNeeded; ++i)
                        {
                            pagesArray.Add(firstPageIndex + i);
                        }
                    }
                    PerTileInstancePages[index] = pagesArray;
                }
            }
        }
        
        [BurstCompile]
        private struct CalculateTotalNumberOfFoundScatteredInstancesJob : IJobFor 
        {
            [NativeDisableParallelForRestriction]
            [ReadOnly] public NativeSlice<int> Source;
            [NativeDisableParallelForRestriction]
            public NativeSlice<int> TargetSums;
            public int NumberOfEntriesPerJob;
            public int StrideIndex;
            public int StrideJobIndex;
            public int AlignValueTo;

            public void Execute(int jobIndex)
            {
                int totalCount = Source.Length;
                int sum = 0;

                for (int i = 0; i < NumberOfEntriesPerJob;++i)
                {
                    int index = StrideJobIndex * jobIndex + i * StrideIndex;
                    if (index >= totalCount) break;
                    int val = Source[index];
                    val = ((val + AlignValueTo - 1) / AlignValueTo) * AlignValueTo;
                    sum += val;
                }
                TargetSums[jobIndex] = sum;
            }
        }
        
        [BurstCompile]
        private unsafe struct ResizeDataStorageJob : IJob
        {
            [NativeDisableUnsafePtrRestriction][NativeDisableParallelForRestriction]
            public NativeReference<UnsafeList<ScatteredInstancePropertiesPacked>> perInstanceData;
            [NativeDisableUnsafePtrRestriction][NativeDisableParallelForRestriction]
            public NativeReference<UnsafeList<Entity>> perInstanceEntity;
            [NativeDisableUnsafePtrRestriction] [NativeDisableParallelForRestriction]
            public int* PagesCounter; 

            public void Execute()
            {
                int size = (*PagesCounter) * INSTANCE_DATA_PAGE_SIZE;
                if (perInstanceData.Value.Length < size)
                {
                    int newSize = (int)(DATA_PAGES_GROW_STRATEGY * size);
                    {
                        var perInstanceDataArray = perInstanceData.Value;
                        perInstanceDataArray.Resize(newSize);
                        perInstanceData.Value = perInstanceDataArray;
                    }

                    {
                        var perInstanceEntityArray = perInstanceEntity.Value;
                        perInstanceEntityArray.Resize(newSize);
                        perInstanceEntity.Value = perInstanceEntityArray;
                    }
                }
            }
        }
        [BurstCompile]
        private struct FillScatteredInstancePartitioningDataJob : IJobChunk 
        {
            [ReadOnly] public EntityTypeHandle EntityTypeHandle;
            [ReadOnly] public ComponentTypeHandle<ScatteredInstancePartitioningData> PartitionDataType;
            [ReadOnly] public ComponentTypeHandle<LocalToWorld> TransformType;
            [ReadOnly] public SharedComponentTypeHandle<ScatteredInstanceSpringData> PhysicsParamsType;
            [ReadOnly] public NativeArray<UnsafeList<int>> PerTileReservedPages;
            [NativeDisableUnsafePtrRestriction][NativeDisableParallelForRestriction]
            public NativeReference<UnsafeList<ScatteredInstancePropertiesPacked>> PerInstanceData;
            [NativeDisableUnsafePtrRestriction][NativeDisableParallelForRestriction]
            public NativeReference<UnsafeList<Entity>> PerInstanceEntity;

            public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask)
            {
                var chunkPartitionData = chunk.GetNativeArray(ref PartitionDataType);
                var chunkTransforms = chunk.GetNativeArray(ref TransformType);
                var chunkEntities = chunk.GetNativeArray(EntityTypeHandle);
                ScatteredInstanceSpringData physicsParams = chunk.GetSharedComponent(PhysicsParamsType);

                Unity.Mathematics.Random rand = Unity.Mathematics.Random.CreateFromIndex((uint)unfilteredChunkIndex);
                
                var enumerator = new ChunkEntityEnumerator(useEnabledMask,
                    chunkEnabledMask, chunk.Count);
                while(enumerator.NextEntityIndex(out int i))
                {
                    var partitionData = chunkPartitionData[i];
                    var transform = chunkTransforms[i];
                    if (partitionData.IndexInTile == -1 || partitionData.FlatTileIndex == -1) continue;
                    
                    var tilePagesMappingArray = PerTileReservedPages[partitionData.FlatTileIndex];
                    var indexToPageMapping = partitionData.IndexInTile / INSTANCE_DATA_PAGE_SIZE;
                    var pageIndex = tilePagesMappingArray[indexToPageMapping];
                    var dataIndex = CalculateDataEntryIndex(pageIndex, partitionData.IndexInTile);
                    var perInstanceDataArray = PerInstanceData.Value;
                    var perInstanceEntityArray = PerInstanceEntity.Value;

                    float3 tipOffset = physicsParams.SpringTipOffset;
                    float tipRadius = physicsParams.SpringTipRadius;

                    float3 tipOffsetWorld = math.mul(transform.Value, new float4(tipOffset, 0.0f)).xyz;
                    float tipRadiusWorld = tipRadius * (math.length(tipOffsetWorld) / math.length(tipOffset));
                    
                    float damping = math.lerp(physicsParams.DampingMinMax.x, physicsParams.DampingMinMax.y,
                        rand.NextFloat());
                    float stiffness = math.lerp(physicsParams.StiffnessMinMax.x, physicsParams.StiffnessMinMax.y,
                        rand.NextFloat());
                    
                    float recoveryAngleRadian = math.lerp(physicsParams.BreakingRecoveryAngleMinMax.x, physicsParams.BreakingRecoveryAngleMinMax.y,
                        rand.NextFloat());
                        
                    float breakingAngle = math.lerp(physicsParams.BreakingAngleMinMax.x, physicsParams.BreakingAngleMinMax.y,
                        rand.NextFloat());

                    recoveryAngleRadian = math.min(breakingAngle, recoveryAngleRadian);
                    float2 plasticityParams = new float2(math.cos(breakingAngle), recoveryAngleRadian); 
                    
                    uint4 springDataPlasticityPacked = new uint4(
                        (math.f32tof16(tipOffsetWorld.x) << 16) | (math.f32tof16(tipOffsetWorld.y) & 0xFFFF),
                        (math.f32tof16(tipOffsetWorld.z) << 16) | (math.f32tof16(tipRadiusWorld) & 0xFFFF),
                        (math.f32tof16(damping) << 16) | (math.f32tof16(stiffness) & 0xFFFF),
                        (math.f32tof16(plasticityParams.x) << 16) | (math.f32tof16(plasticityParams.y) & 0xFFFF)
                    );
                    
                    perInstanceDataArray[dataIndex] =
                        new ScatteredInstancePropertiesPacked()
                        {
                            _PositionFlags = new uint4(math.asuint(transform.Position), 0), //should maybe take mesh bounds and apply offset to position so that the instance position is always the root even if the mesh is centered around origo
                            _SpringDataPlasticityPacked = springDataPlasticityPacked, 
                        };
                    var ent = chunkEntities[i];
                    perInstanceEntityArray[dataIndex] = ent;
                }
            }
        }
 #endregion
 
 
        [BurstCompile]
        JobHandle RemoveInstancesFromPartitioning(ref SystemState state, JobHandle handle, ref ScatteredInstanceSpatialPartitioningData partitioningData, NativeArray<int> ChangesInTileArray)
        {
            var entryCount = m_RemovedEntitiesQuery.CalculateEntityCount();
            NativeParallelMultiHashMap<int, Entity> removedEntries = new NativeParallelMultiHashMap<int, Entity>(entryCount, Allocator.TempJob); 
            
            var jobHandle = new GatherRemovedEntitiesPerTile()
            {
                RemovedEntries = removedEntries.AsParallelWriter(),
                EntityTypeHandle = m_EntityTypeHandle,
                PartitionedDataLookup = m_PartitionedDataLookup,
            }.ScheduleParallel(m_RemovedEntitiesQuery, handle);
            
            
            jobHandle = new RemoveEntitiesFromPartitioningJob()
            {
                RemovedEntries = removedEntries.AsReadOnly(),
                InstanceCountsPerTile = partitioningData.InstancesPerTile,
                ChangesInTile = ChangesInTileArray,
                EntityHandlePerDataEntry = partitioningData.EntityHandlePerDataEntry.Value,
                InstanceDataPages = partitioningData.InstanceDataPages.Value,
                PerTileInstanceData = partitioningData.PerTileReservedPages,
                PartitionedDataLookup = m_PartitionedDataLookup,
                ChildTileDataLookup = m_RenderDataLookup,
                InstanceChildrenLookup = m_InstanceChildrenArrayLookup
            }.Schedule(ChangesInTileArray.Length, 64, jobHandle);

            return jobHandle;
        }


        [BurstCompile]
        JobHandle AddNewInstanceToPartitioning(ref SystemState state, JobHandle handle, ref ScatteredInstanceSpatialPartitioningData partitioningData, NativeArray<int> ChangesInTileArray)
        {
            var jobHandle = new AddEntitiesToPartitioningJob()
            {
                CellSizeInv = 1.0f/partitioningData.CellSizeInMeters,
                TransformType = m_TransformType,
                ScatteredPartitioningData = m_PartitionedDataTypeWrite,
                GridResolution = partitioningData.CalculateGridResolution(),
                WorldCorner = new float2(partitioningData.CanvasArea.Min.xz),
                InstanceCountsPerTile = partitioningData.InstancesPerTile,
                ChangesInTile = ChangesInTileArray,
                ChildBufferLookup = m_ChildBufferLookup,
                RenderTileDataLookup =  m_RenderDataLookup,
                EntityType = m_EntityTypeHandle
            }.ScheduleParallel(m_NonPartitionedInstancesQuery, handle);
            
            return jobHandle;
        }
        
        [BurstCompile]
        JobHandle UpdateTileRevisionNumber(ref SystemState state, JobHandle handle, ref ScatteredInstanceSpatialPartitioningData partitioningData, NativeArray<int> ChangesInTileArray)
        {
            
            
            var jobHandle = new UpdateTileRevisionNumberJob()
            {
                ChangesInTile = ChangesInTileArray,
                TileRevision = partitioningData.TileRevisionNumber
            }.Schedule(partitioningData.TileRevisionNumber.Length, 128, handle);
            
            return jobHandle;
        }
        
        [BurstCompile]
        JobHandle ReserveRequiredPages(ref SystemState state, JobHandle handle, ref ScatteredInstanceSpatialPartitioningData partitioningData)
        {
            unsafe
            {
                //make sure we have enough space to write released pages (Could actually count how many we need, but for now just assume worst case which is all pages released)
                if (partitioningData.FreePages.Length < *partitioningData.ReservedPagesCounter)
                {
                    var freePages = partitioningData.FreePages;
                    freePages.ResizeArray(*partitioningData.ReservedPagesCounter);
                    partitioningData.FreePages = freePages;
                }
                
                var jobHandle = new FreeUnusedPagesJob()
                {
                    InstancesPerTile = partitioningData.InstancesPerTile,
                    PerTileInstancePages = partitioningData.PerTileReservedPages,
                    FreePages = partitioningData.FreePages,
                    FreePagesCounter = partitioningData.FreePagesCounter
                
                }.ScheduleParallel(partitioningData.InstancesPerTile.Length, 64, handle);
                
                
                
                jobHandle = new ResizePerTileArraysAndReservePagesJob()
                {
                    InstancesPerTile = partitioningData.InstancesPerTile,
                    PerTileInstancePages = partitioningData.PerTileReservedPages,
                    PagesCounter = partitioningData.ReservedPagesCounter,
                    FreePages = partitioningData.FreePages,
                    FreePagesCounter = partitioningData.FreePagesCounter
                }.ScheduleParallel(partitioningData.InstancesPerTile.Length, 64, jobHandle);
                return jobHandle;
            }
        }
        
        [BurstCompile]
        JobHandle EnsureEnoughDataStorage(ref SystemState state, JobHandle handle, ref ScatteredInstanceSpatialPartitioningData partitioningData)
        {
            ScatteredInstanceSpatialPartitioningData partitioningDataComponent =  state.EntityManager.GetComponentData<ScatteredInstanceSpatialPartitioningData>(state.SystemHandle);
            unsafe
            {
                var jobHandle = new ResizeDataStorageJob()
                {
                    PagesCounter = partitioningDataComponent.ReservedPagesCounter,
                    perInstanceData = partitioningDataComponent.InstanceDataPages,
                    perInstanceEntity = partitioningDataComponent.EntityHandlePerDataEntry
                }.Schedule(handle);

                return jobHandle;
            }
            
        }
        
        
        [BurstCompile]
        JobHandle FillScatteredInstancePartitioningData(ref SystemState state, JobHandle handle, ref ScatteredInstanceSpatialPartitioningData partitioningData)
        {

            var jobHandle = new FillScatteredInstancePartitioningDataJob()
            {
                PartitionDataType = m_PartitionedDataType,
                TransformType = m_TransformType,
                EntityTypeHandle = m_EntityTypeHandle,
                PerInstanceData = partitioningData.InstanceDataPages,
                PerInstanceEntity = partitioningData.EntityHandlePerDataEntry,
                PerTileReservedPages = partitioningData.PerTileReservedPages,
                PhysicsParamsType = m_PhysicsParametersType
            }.ScheduleParallel(m_NonPartitionedInstancesQuery, handle);
            
            
            return jobHandle;
        }
        
    }
}
