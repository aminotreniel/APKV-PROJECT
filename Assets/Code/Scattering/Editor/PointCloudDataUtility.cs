using System;
using System.Collections.Generic;
using System.Threading;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Mathematics;

namespace TimeGhost
{
    public static class PointCloudUtility
    {
        public struct PointCloudDataEntry
        {
            public PointCloudFromHoudiniAsset.PointCloudData pcData;
            public float4x4 transform;
        }

        public struct PointCloudPartitionOutput
        {
            public ScatterPointCloudSystem.PartitioningInfo partitioningInfo;
            public NativeArray<float3> transformedPositions;
            public NativeArray<int2> toPointCloudDataEntryMapping; //mapping from a flattened index to the PointCloudDataEntry index (x) and index in the given PointCloudData (y) 
            public NativeList<int> numberOfEntriesPerCell; //number of points in a given cell
            public NativeArray<int> offsetPerCell; //offset to cellToIndexMapping for a given cell
            public NativeArray<int2> pointCloudEntryOffsetAndCountPerCell; //offset for points on a given cell coming from a given point cloud (indexing is pointCloudIndex * cellCount + cellIndex)
            public NativeArray<int> cellToIndexMapping; //cell to indices mapping
            
            public JobHandle pendingJobs;

            public void Dispose(JobHandle handle = default)
            {

                if (transformedPositions.IsCreated)
                {
                    transformedPositions.Dispose();
                }
                if (toPointCloudDataEntryMapping.IsCreated)
                {
                    toPointCloudDataEntryMapping.Dispose(handle);
                }
                
                if (numberOfEntriesPerCell.IsCreated)
                {
                    numberOfEntriesPerCell.Dispose(handle);
                }
                
                if (offsetPerCell.IsCreated)
                {
                    offsetPerCell.Dispose(handle);
                }

                if (pointCloudEntryOffsetAndCountPerCell.IsCreated)
                {
                    pointCloudEntryOffsetAndCountPerCell.Dispose(handle);
                }
                
                if (cellToIndexMapping.IsCreated)
                {
                    cellToIndexMapping.Dispose(handle);
                }
            }
        }

        public static PointCloudPartitionOutput PartitionPointCloudDataToTiles(PointCloudDataEntry[] entries, float tileSize, Allocator allocator)
        {
            int overallPointCount = 0;
            {
                foreach (var source in entries)
                {
                    overallPointCount += source.pcData.positions.Length;
                }
            }

            PointCloudDataEntry[] dataArray = entries;
            NativeArray<float3> positions = new NativeArray<float3>(overallPointCount, allocator);
            NativeArray<int2> toPointCloudDataMapping = new NativeArray<int2>(overallPointCount, allocator);

            MinMaxAABB overallBounds = MinMaxAABB.Empty;
            int index = 0;
            for (int i = 0; i < dataArray.Length; ++i)
            {
                PointCloudDataEntry entry = dataArray[i];
                for (int k = 0; k < entry.pcData.positions.Length; ++k)
                {
                    float3 p = entry.pcData.positions[k];
                    p = math.mul(entry.transform, new float4(p.x, p.y, p.z, 1)).xyz;
                    overallBounds.Encapsulate(p);
                    var ind = index++;
                    positions[ind] = p;
                    toPointCloudDataMapping[ind] = new int2(i, k);
                }
            }

            // If we only had a single point we'll end up with zero-sized bounding box. This breaks further down
            // the processing chain  when we try to calculate cell counts from bounds size.
            if (math.all(overallBounds.Min == overallBounds.Max))
            {
                overallBounds.Max += 1e-2f; // can't go too small since this will mix with large values
            }

            ScatterPointCloudSystem.PartitioningInfo partitioningInfo = new ScatterPointCloudSystem.PartitioningInfo();
            partitioningInfo.cellSize = tileSize;
            partitioningInfo.bounds = overallBounds;

            const int ENTRIES_PER_THREAD = 64;

            var cellCount = partitioningInfo.GetNumberOfCells().x * partitioningInfo.GetNumberOfCells().y;

            NativeList<int> numberOfEntriesPerCell = new NativeList<int>(cellCount, allocator);
            numberOfEntriesPerCell.AddReplicate(0, cellCount);

            NativeArray<int> offsetPerCell = new NativeArray<int>(cellCount, allocator);
            NativeArray<int> cellToIndexMapping = new NativeArray<int>(overallPointCount, allocator);
            NativeArray<int2> pointCloudEntryOffsetAndCountPerCell = new NativeArray<int2>(cellCount * dataArray.Length, allocator);

            JobHandle jobHandle = default;
            unsafe
            {
                jobHandle = new CalculateEntriesPerCell()
                {
                    Points = (float3*)positions.GetUnsafePtr(),
                    NumberOfEntriesPerCell = numberOfEntriesPerCell,
                    PartitioningInfo = partitioningInfo,
                }.Schedule(overallPointCount, ENTRIES_PER_THREAD, jobHandle);

                jobHandle = new CreatePerCellOffsets()
                {
                    NumberOfEntriesPerCell = numberOfEntriesPerCell,
                    OffsetPerCell = offsetPerCell
                }.Schedule(jobHandle);

                int currentCombinedPositionsOffset = 0; //preserve relative ordering of points: all points from first entry before the second and so on
                for (int i = 0; i < dataArray.Length; ++i)
                {
                    var pointsCount = dataArray[i].pcData.positions.Length;

                    jobHandle = new AssignIndicesToCells()
                    {
                        Points = (float3*)positions.GetUnsafePtr(),
                        NumberOfEntriesPerCell = numberOfEntriesPerCell,
                        OffsetPerCell = offsetPerCell,
                        PartitioningInfo = partitioningInfo,
                        IndicesPerCell = cellToIndexMapping,
                        PointsOffsetForBatch = currentCombinedPositionsOffset
                    }.Schedule(pointsCount, ENTRIES_PER_THREAD, jobHandle);

                    currentCombinedPositionsOffset += pointsCount;
                }
            }
            
            jobHandle = new GeneratePerCellPerPointCloudOffsetAndCount()
            {
                CellCount = cellCount,
                PointCloudCount = dataArray.Length,
                NumberOfEntriesPerCell = numberOfEntriesPerCell,
                OffsetPerCell = offsetPerCell,
                IndicesPerCell = cellToIndexMapping,
                ToPointCloudDataEntryMapping = toPointCloudDataMapping,
                PointCloudEntryOffsetAndCountPerCell = pointCloudEntryOffsetAndCountPerCell
            }.Schedule(cellCount, 1, jobHandle);
            
            

            return new PointCloudPartitionOutput()
            {
                numberOfEntriesPerCell = numberOfEntriesPerCell,
                offsetPerCell = offsetPerCell,
                partitioningInfo = partitioningInfo,
                pendingJobs = jobHandle,
                toPointCloudDataEntryMapping = toPointCloudDataMapping,
                transformedPositions = positions,
                cellToIndexMapping = cellToIndexMapping,
                pointCloudEntryOffsetAndCountPerCell = pointCloudEntryOffsetAndCountPerCell
            };
        }


        [BurstCompile]
        private unsafe struct CalculateEntriesPerCell : IJobParallelFor
        {
            [NativeDisableUnsafePtrRestriction] public float3* Points;
            [NativeDisableParallelForRestriction] public NativeList<int> NumberOfEntriesPerCell;
            public ScatterPointCloudSystem.PartitioningInfo PartitioningInfo;
            
            public void Execute(int index)
            {
                int cellIndex = PartitioningInfo.GetFlatCellIndex(Points[index]);
                Interlocked.Add(ref NumberOfEntriesPerCell.ElementAt(cellIndex), 1);
                
            }
        }

        //lazy prefix sum todo: parallelize
        [BurstCompile]
        private struct CreatePerCellOffsets : IJob
        {
            public NativeList<int> NumberOfEntriesPerCell;
            public NativeArray<int> OffsetPerCell;

            public void Execute()
            {
                int sum = 0;
                for (int i = 0; i < NumberOfEntriesPerCell.Length; ++i)
                {
                    int val = NumberOfEntriesPerCell[i];
                    OffsetPerCell[i] = sum;
                    sum += val;
                }

                var entries = NumberOfEntriesPerCell.Length;
                NumberOfEntriesPerCell.Clear();
                NumberOfEntriesPerCell.AddReplicate(0, entries);
            }
        }
        
        [BurstCompile]
        private unsafe struct AssignIndicesToCells : IJobParallelFor
        {
            [NativeDisableUnsafePtrRestriction] [ReadOnly]
            public float3* Points;

            [NativeDisableParallelForRestriction] public NativeList<int> NumberOfEntriesPerCell;
            [ReadOnly] public NativeArray<int> OffsetPerCell;
            [NativeDisableParallelForRestriction] public NativeArray<int> IndicesPerCell;
            public ScatterPointCloudSystem.PartitioningInfo PartitioningInfo;
            public int PointsOffsetForBatch;
            
            
            public void Execute(int index)
            {
                var ind = PointsOffsetForBatch + index;
                
                int cellIndex = PartitioningInfo.GetFlatCellIndex(Points[ind]);
                int cellOffset = OffsetPerCell[cellIndex];
                int offsetInCell = Interlocked.Add(ref NumberOfEntriesPerCell.ElementAt(cellIndex), 1) - 1;
                IndicesPerCell[cellOffset + offsetInCell] = ind;
            }
        }
        
        
        [BurstCompile]
        private struct GeneratePerCellPerPointCloudOffsetAndCount : IJobParallelFor
        {
            public int PointCloudCount;
            public int CellCount;
            [ReadOnly] public NativeArray<int2> ToPointCloudDataEntryMapping;
            [ReadOnly] public NativeArray<int> OffsetPerCell;
            [ReadOnly] public NativeList<int> NumberOfEntriesPerCell;
            [ReadOnly] public NativeArray<int> IndicesPerCell;
            
            [NativeDisableParallelForRestriction] 
            public NativeArray<int2> PointCloudEntryOffsetAndCountPerCell;

            public void Execute(int cellIndex)
            {
                int offset = OffsetPerCell[cellIndex];
                int count = NumberOfEntriesPerCell[cellIndex];
                
                //initialize PointCloudEntryOffsetAndCountPerCell
                for (int i = 0; i < PointCloudCount; ++i)
                {
                    int index = i * CellCount + cellIndex;
                    PointCloudEntryOffsetAndCountPerCell[index] = new int2(offset, 0);
                }

                if (count == 0) return;

                int currentPcIndex = -1;
                int currentCount = 0;
                int currentOffset = offset;
                for (int i = 0; i < count; ++i)
                {
                    int pointIndex = IndicesPerCell[offset + i];
                    int2 pcIndexOffset = ToPointCloudDataEntryMapping[pointIndex];
                    if (pcIndexOffset.x != currentPcIndex)
                    {
                        if (currentPcIndex != -1)
                        {
                            int index = currentPcIndex * CellCount + cellIndex;
                            PointCloudEntryOffsetAndCountPerCell[index] = new int2(currentOffset, currentCount);
                        }
                        currentPcIndex = pcIndexOffset.x;
                        currentCount = 1;
                        currentOffset = offset + i;
                    }
                    else
                    {
                        ++currentCount;
                    }
                }

                {
                    int index = currentPcIndex * CellCount + cellIndex;
                    PointCloudEntryOffsetAndCountPerCell[index] = new int2(currentOffset, currentCount);
                }
                
            }
        }
    }
}