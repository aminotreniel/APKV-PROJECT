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
using UnityEngine;

namespace TimeGhost
{
    public partial class ScatteredInstanceInteractionSystem : SystemBase
    {
        [BurstCompile]
        private struct CheckTilesModifiedJob : IJobParallelFor
        {
            [NativeDisableParallelForRestriction]
            public NativeArray<int> PageChangesFoundPerTile;
            [NativeDisableParallelForRestriction] 
            public NativeArray<int> LastSeenTileRevisions;
            [ReadOnly] 
            public NativeArray<int> TileRevisions;
            
            public uint2 TilesPerActiveDimension;
            public uint2 ActiveAreaCenter;
            public uint2 AbsoluteGridResolution;
  
            
            public void Execute(int index)
            {
                int activeTileIndex = index;
                int absoluteTileIndex = ActiveFlattenedTileIndexToAbsoluteFlattenedTileIndex((uint)activeTileIndex, TilesPerActiveDimension,
                    ref ActiveAreaCenter, ref AbsoluteGridResolution);

                int currentTileRevision = TileRevisions[absoluteTileIndex];
                int previousTileRevision = LastSeenTileRevisions[activeTileIndex];


                if (currentTileRevision != previousTileRevision)
                {
                    PageChangesFoundPerTile[activeTileIndex] = 1;
                    LastSeenTileRevisions[activeTileIndex] = currentTileRevision;
                }
                else
                {
                    PageChangesFoundPerTile[activeTileIndex] = 0;
                }
                
            }
        }
        
        [BurstCompile]
        private struct CalculateTileRefreshParamsJob : IJobParallelFor
        {
            [NativeDisableUnsafePtrRestriction]
            public UnsafeAtomicCounter32 TotalPageCount;
            [NativeDisableParallelForRestriction]
            public NativeArray<int3> ReserveTilesParams;
            [NativeDisableParallelForRestriction]
            public NativeArray<int> FreeTilesParams;
            [NativeDisableParallelForRestriction] 
            public NativeArray<int> PageCountPerActiveTile;
            [NativeDisableParallelForRestriction] 
            public NativeQueue<UploadEntry>.ParallelWriter UploadQueue;
            [NativeDisableParallelForRestriction]
            public NativeArray<uint> PerTileUploadVersion;

            [ReadOnly]
            public NativeList<int> ActiveTilesToRefresh;
            [ReadOnly]
            public NativeArray<int> InstancesPerAbsoluteTile;
            
            
            public uint2 TilesPerActiveDimension;
            public uint2 ActiveAreaCenter;
            public uint2 AbsoluteGridResolution;
  
            
            public void Execute(int index)
            {
                int pageSize = ScatteredInstanceInteractionGPU.GetPageSize();
                int activeTileToRefresh = ActiveTilesToRefresh[index];
                int absoluteTileIndex = ActiveFlattenedTileIndexToAbsoluteFlattenedTileIndex((uint)activeTileToRefresh, TilesPerActiveDimension,
                    ref ActiveAreaCenter, ref AbsoluteGridResolution);

                int instanceCount = InstancesPerAbsoluteTile[absoluteTileIndex];
                int newActivePageCount = (instanceCount + pageSize - 1) / pageSize;
                int oldActivePageCount = PageCountPerActiveTile[activeTileToRefresh];

                int pageCountChange = newActivePageCount - oldActivePageCount;

                //update active page counts
                PageCountPerActiveTile[activeTileToRefresh] = newActivePageCount;
                TotalPageCount.Add(pageCountChange);
                
                //fill free tile list (just copy tile index)
                FreeTilesParams[index] = activeTileToRefresh;
                
                //fill reserve pages entry
                ReserveTilesParams[index] = new int3(activeTileToRefresh, newActivePageCount, instanceCount); //x == tile index, y = pageCount, z = actual entry count 

                uint uploadVersioning = PerTileUploadVersion[activeTileToRefresh];
                uploadVersioning += 1;
                PerTileUploadVersion[activeTileToRefresh] = uploadVersioning;
                
                if (instanceCount > 0)
                {
                    int instancesLeftToupload = instanceCount;
                    for (int i = 0; i < newActivePageCount; ++i)
                    {
                        int uploadBatchSize = math.min(instancesLeftToupload, pageSize);
                        UploadQueue.Enqueue(new UploadEntry{absoluteTileIndex = absoluteTileIndex, activeTileIndex = activeTileToRefresh, entryCount = uploadBatchSize, isLastUploadBatchForTile = i == (newActivePageCount - 1), pageOffset = i, uploadVersioning = uploadVersioning});
                        instancesLeftToupload -= pageSize;
                    }
                }
                else
                {
                    UploadQueue.Enqueue(new UploadEntry{absoluteTileIndex = absoluteTileIndex, activeTileIndex = activeTileToRefresh, entryCount = 0, isLastUploadBatchForTile = true, pageOffset = 0, uploadVersioning = uploadVersioning});
                }
            }
        }
        
        [BurstCompile]
        private struct FillNextUploadBatchJob : IJobParallelFor
        {
            [NativeDisableParallelForRestriction]
            public NativeArray<ScatteredInstancePropertiesPacked> UploadDataBuffer;
            [NativeDisableParallelForRestriction]
            public NativeArray<ScatteredInstanceDataUploadBatch> UploadDataBatch;
            [ReadOnly] 
            public NativeList<UploadEntry> UploadBatchEntries;
            [ReadOnly]
            public UnsafeList<ScatteredInstancePropertiesPacked> InstanceDataPages;
            [ReadOnly]
            public NativeArray<UnsafeList<int>> PerTileReservedPages;

            public void Execute(int index)
            {
                UploadEntry entry = UploadBatchEntries[index];
                int absoluteTilePageOffset = (entry.pageOffset * ScatteredInstanceInteractionGPU.GetPageSize() )/ ScatteredInstanceSpatialPartitioningSystem.INSTANCE_DATA_PAGE_SIZE;
                int absoluteTilePage = PerTileReservedPages[entry.absoluteTileIndex][absoluteTilePageOffset];
                int offsetInAbsoluteTilePage = (entry.pageOffset * ScatteredInstanceInteractionGPU.GetPageSize()) % ScatteredInstanceSpatialPartitioningSystem.INSTANCE_DATA_PAGE_SIZE;

                int instanceOffsetSrc = offsetInAbsoluteTilePage +
                    absoluteTilePage * ScatteredInstanceSpatialPartitioningSystem.INSTANCE_DATA_PAGE_SIZE;
                int instanceOffsetDst = index * ScatteredInstanceInteractionGPU.GetPageSize();

                for (int i = 0; i < entry.entryCount; ++i)
                {
                    ScatteredInstancePropertiesPacked props = InstanceDataPages[instanceOffsetSrc + i];
                    UploadDataBuffer[instanceOffsetDst + i] = props;
                    
                }

                UploadDataBatch[index] = new ScatteredInstanceDataUploadBatch()
                {
                    entryCount = entry.entryCount, padding = 0, tileIndex = entry.activeTileIndex,
                    perTilePageOffset = entry.pageOffset
                };
            }
        }
        
        [BurstCompile]
        private struct GatherRelevantCollidersJob : IJobChunk 
        {
            [NativeDisableUnsafePtrRestriction]
            public UnsafeAtomicCounter32 ColliderCount;
            [NativeDisableParallelForRestriction]
            public NativeArray<ScatteredInstanceInteractionGPU.CapsuleColliderEntry> CollidersArray;

            [ReadOnly] public ComponentTypeHandle<ScatteredInstanceColliderData> ColliderDataType;
            [ReadOnly] public ComponentTypeHandle<LocalToWorld> TransformType;

            public float2 WorldCorner;
            public float CellSizeInMeters;
            public uint2 ActiveTilesPerDimension;
            public uint2 ActiveAreaCenter;

            public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask)
            {
                const float EPSILON = 1e-6f;
                
                var colliderDatas = chunk.GetNativeArray(ref ColliderDataType);
                var chunkTransforms = chunk.GetNativeArray(ref TransformType);

                uint2 tilesPerDirection = ActiveTilesPerDimension / 2;
                float2 activeAreaMin = WorldCorner + (ActiveAreaCenter - tilesPerDirection) * new float2(CellSizeInMeters);
                float2 activeAreaMax = WorldCorner + (ActiveAreaCenter + tilesPerDirection) * new float2(CellSizeInMeters);

                var enumerator = new ChunkEntityEnumerator(useEnabledMask,
                    chunkEnabledMask, chunk.Count);
                while (enumerator.NextEntityIndex(out int i))
                {
                    var colliderData = colliderDatas[i];
                    var transform = chunkTransforms[i];
                    float3 wp0 = math.mul(transform.Value, new float4(colliderData.P0, 1)).xyz;
                    float3 wp1 = math.mul(transform.Value, new float4(colliderData.P1, 1)).xyz;
                    float3 d = wp0 - wp1;
                    
                    
                    float radius;
                    if (math.dot(d, d) > EPSILON)
                    {
                        float3 capsuleNormal = math.normalize(math.cross(wp0, wp1));
                        capsuleNormal = math.mul(transform.Value, new float4(capsuleNormal, 0)).xyz;
                        radius = colliderData.Radius * math.length(capsuleNormal);
                    }
                    else
                    {
                        float3 v = new float3(0.577350f, 0.577350f, 0.577350f);
                        float approxScale = math.length(math.mul(transform.Value, new float4(v, 0)).xyz);
                        radius = colliderData.Radius * approxScale;
                    }
                    
                    float2 pos0 = wp0.xz;
                    float2 pos1 = wp1.xz;

                    float2 minCorner = math.min(pos0 - radius, pos1 - radius);
                    float2 maxCorner = math.max(pos0 + radius, pos1 + radius);
                    
                    
                    if (math.any(minCorner > activeAreaMax) || math.any(maxCorner < activeAreaMin))
                    {
                        continue;
                    }
                    
                    //collider is inside active area
                    int colliderArrayIndex = ColliderCount.Add(1);
                    CollidersArray[colliderArrayIndex] = new ScatteredInstanceInteractionGPU.CapsuleColliderEntry {wp0Radius = new float4(wp0, radius), wp1 = new float4(wp1, 1)};

                }
            }
        }
        
        [BurstCompile]
        private struct ClearColliderPerTileMasksJob : IJobParallelFor
        {
            
            [NativeDisableParallelForRestriction]
            public NativeArray<int> Masks;

            public void Execute(int index)
            {
                Masks[index] = 0;
            }
        }
        
        [BurstCompile]
        private struct GatherTilesAffectedByCollidersJob : IJobParallelFor 
        {
            [NativeDisableUnsafePtrRestriction]
            public UnsafeAtomicCounter32 ActiveTileCount;
            [NativeDisableParallelForRestriction]
            public NativeArray<int> PerTileColliderMask;
            [NativeDisableParallelForRestriction] 
            public NativeArray<uint> ColliderAffectedTilesPageCount;
            [NativeDisableParallelForRestriction] 
            public NativeArray<int> ColliderAffectedTiles;
            
            [ReadOnly]
            public NativeArray<ScatteredInstanceInteractionGPU.CapsuleColliderEntry> CollidersArray;
            [ReadOnly] 
            public NativeArray<int> PagesPerTile;

            public float2 WorldCorner;
            public float CellSizeInMeters;
            public uint2 ActiveTilesPerDimension;
            public uint2 ActiveAreaCenter;
            
            public int ColliderOffset;

            public void Execute(int index)
            {
                var collider = CollidersArray[ColliderOffset + index];
                float cellSizeInv = 1.0f / CellSizeInMeters;

                //the index should always be < 32
                int colliderMask = 1 << index;

                CalculateMaxActiveRegionOverlapWithCapsule(collider.wp0Radius.xz, collider.wp1.xz, collider.wp0Radius.w, WorldCorner, ActiveAreaCenter, cellSizeInv, ActiveTilesPerDimension, out var colliderTileMin, out var colliderTileMax);
                for (int y = colliderTileMin.y; y <= colliderTileMax.y; ++y)
                {
                    for (int x = colliderTileMin.x; x <= colliderTileMax.x; ++x)
                    {
                        float2 tileCenter = WorldCorner + new float2(x * CellSizeInMeters, y * CellSizeInMeters);

                        //if (CheckTileCapsuleCollision(ref pos0, ref pos1, radius, ref tileCenter, CellSizeInMeters))
                        {
                            int tileIndex = AbsoluteToActiveFlattenedTileIndex(x, y, (int2)ActiveTilesPerDimension);

                            unsafe
                            {
                                //no interlocked or, use add to add mask
                                int previousValue = Interlocked.Add(ref ((int*)PerTileColliderMask.GetUnsafePtr())[tileIndex], colliderMask) - colliderMask;
                                if (previousValue == 0)
                                {
                                    int activeTileListIndex = ActiveTileCount.Add(1);
                                    uint pageCountInTile = (uint)PagesPerTile[tileIndex];
                                    ColliderAffectedTilesPageCount[activeTileListIndex] = pageCountInTile;
                                    ColliderAffectedTiles[activeTileListIndex] = tileIndex;
                                }
                            }
                        }
                    }
                }
            }
        }
        
        
        [BurstCompile]
        private struct ResizeActiveColliderTilesArray : IJob
        {
            [NativeDisableParallelForRestriction]
            public NativeArray<uint> ColliderAffectedTilesPageCount;
            [NativeDisableParallelForRestriction]
            public NativeReference<UnsafeList<uint3>> ColliderIntersectingTilePageAndMask;
            [ReadOnly][NativeDisableUnsafePtrRestriction]
            public UnsafeAtomicCounter32 AffectedTilesCount;

            public void Execute()
            {
                int tilesCount;
                unsafe
                {
                    tilesCount = *AffectedTilesCount.Counter;
                }
                
                if (tilesCount > 0)
                {
                    uint sum = 0;
                    for (int i = 0; i < tilesCount; ++i)
                    {
                        uint val = ColliderAffectedTilesPageCount[i];
                        ColliderAffectedTilesPageCount[i] = sum;
                        sum += val;
                    }

                    ColliderAffectedTilesPageCount[tilesCount] = sum;

                    UnsafeList<uint3> pagesArray = ColliderIntersectingTilePageAndMask.Value;
                    if (!pagesArray.IsCreated || pagesArray.Length < sum)
                    {
                        if (!pagesArray.IsCreated)
                        {
                            pagesArray = new UnsafeList<uint3>((int)sum, Allocator.Persistent);
                        }
                        
                        pagesArray.Resize((int)sum);
                        ColliderIntersectingTilePageAndMask.Value = pagesArray;
                    }
                }


            }
        }
        
        
        [BurstCompile]
        private struct CalculateAffectedTilesPagesAndMaskJob : IJobParallelFor
        {
            
            [NativeDisableParallelForRestriction]
            public NativeReference<UnsafeList<uint3>> ColliderIntersectingTilePageAndMaskArray;
            [ReadOnly]
            public NativeArray<uint> PerTileOffsetToIntersectingTilePages;
            [ReadOnly]
            public NativeArray<int> PerTileColliderMask;
            [ReadOnly] 
            public NativeArray<int> ColliderAffectedTiles;

            
            public void Execute(int index)
            {
                int tileIndex = ColliderAffectedTiles[index];
                int mask = PerTileColliderMask[tileIndex];
                var pagesArray = ColliderIntersectingTilePageAndMaskArray.Value;
                uint tileOffsetIntoTilePagesArray = PerTileOffsetToIntersectingTilePages[index];
                int totalPageCountForTile = (int)PerTileOffsetToIntersectingTilePages[index + 1] - (int)tileOffsetIntoTilePagesArray;

                for (int i = 0; i < totalPageCountForTile; ++i)
                {
                    pagesArray[(int)(tileOffsetIntoTilePagesArray + i)] = new uint3((uint)tileIndex, (uint)i, (uint)mask);
                }
            }
        }
        
    }
}
