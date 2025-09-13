using System;
using System.Threading;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;
using Random = Unity.Mathematics.Random;
using SortableWeightEntry = TimeGhost.TileImpostorUtils.SortableWeightEntry<int>;
using SortableWeightComparer = TimeGhost.TileImpostorUtils.SortableWeightComparer<int>;

namespace TimeGhost
{
    public static class TileImpostorMeshUtility
    {

        private static void CalculateHeightGridData(AABB bounds, ImpostorScatteringRenderer.Batch[] batches, int2 heightGridResolution, int holeFilterMaxRadius, Allocator allocator,
        out NativeArray<float> cellHeightMinOut, out NativeArray<float> cellHeightMaxOut, out NativeArray<float> maxInstanceHeightPerCell, out NativeArray<int> entriesPerCellOut)
        {
            NativeArray<float> heightsMax = new NativeArray<float>(heightGridResolution.x * heightGridResolution.y, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);
            heightsMax.FillArray(float.MinValue);
            NativeArray<float> heightsMin = new NativeArray<float>(heightGridResolution.x * heightGridResolution.y, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);
            heightsMin.FillArray(float.MaxValue);
            NativeArray<float> maxInstanceHeight = new NativeArray<float>(heightGridResolution.x * heightGridResolution.y, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);
            maxInstanceHeight.FillArray(0);
            NativeArray<int> entriesPerCell = new NativeArray<int>(heightGridResolution.x * heightGridResolution.y, allocator, NativeArrayOptions.UninitializedMemory);
            entriesPerCell.FillArray(0);
            unsafe
            {
                JobHandle jobHandle = default;
                for (int i = 0; i < batches.Length; ++i)
                {
                    fixed (Matrix4x4* matrices = batches[i].transforms)
                    {
                        jobHandle = new CalculateHeight()
                        {
                            MeshBounds = batches[i].mesh.bounds.ToAABB(),
                            AreaBounds = bounds,
                            Transforms = matrices,
                            HeightGridResolution = heightGridResolution,
                            HeightsMaxOutput = heightsMax,
                            HeightsMinOutput = heightsMin,
                            MaxInstanceHeight = maxInstanceHeight,
                            NumberOfEntriesOutput = entriesPerCell
                        }.Schedule(batches[i].transforms.Length, 8, jobHandle);
                    }

                    jobHandle.Complete();
                }
            }

            //Fill holes in height
            {
                NativeArray<float> heightsMaxFiltered = new NativeArray<float>(heightGridResolution.x * heightGridResolution.y, allocator, NativeArrayOptions.UninitializedMemory);
                NativeArray<float> heightsMinFiltered = new NativeArray<float>(heightGridResolution.x * heightGridResolution.y, allocator, NativeArrayOptions.UninitializedMemory);
                NativeArray<float> instanceHeightFiltered = new NativeArray<float>(heightGridResolution.x * heightGridResolution.y, allocator, NativeArrayOptions.UninitializedMemory);
                int filterMaxRadius = holeFilterMaxRadius;

                JobHandle jobHandle0 = new FillHoles()
                {
                    Source = heightsMin,
                    EmptyValue = float.MaxValue,
                    HeightGridResolution = heightGridResolution,
                    Output = heightsMinFiltered,
                    SearchRadiusPerDimension = filterMaxRadius
                }.Schedule(heightGridResolution.x * heightGridResolution.y, 16);

                JobHandle jobHandle1 = new FillHoles()
                {
                    Source = heightsMax,
                    EmptyValue = float.MinValue,
                    HeightGridResolution = heightGridResolution,
                    Output = heightsMaxFiltered,
                    SearchRadiusPerDimension = filterMaxRadius
                }.Schedule(heightGridResolution.x * heightGridResolution.y, 16);
                
                JobHandle jobHandle2 = new FillHoles()
                {
                    Source = maxInstanceHeight,
                    EmptyValue = 0,
                    HeightGridResolution = heightGridResolution,
                    Output = instanceHeightFiltered,
                    SearchRadiusPerDimension = filterMaxRadius
                }.Schedule(heightGridResolution.x * heightGridResolution.y, 16);

                jobHandle0.Complete();
                jobHandle1.Complete();
                jobHandle2.Complete();

                heightsMax.Dispose();
                heightsMin.Dispose();
                maxInstanceHeight.Dispose();

                heightsMax = heightsMaxFiltered;
                heightsMin = heightsMinFiltered;
                maxInstanceHeight = instanceHeightFiltered;
            }

            cellHeightMinOut = heightsMin;
            cellHeightMaxOut = heightsMax;
            maxInstanceHeightPerCell = maxInstanceHeight;
            entriesPerCellOut = entriesPerCell;
        }
        
        public static Mesh CreateImpostorMeshRectanglePattern(AABB bounds, ImpostorScatteringRenderer.Batch[] batches, int2 meshResolution, float verticesPerMeter, bool positionToBoundsCenter)
        {
            int meshResolutionFlat = meshResolution.x * meshResolution.y;
            int2 heightGridResolution = (meshResolution + 1) / 2;
            
            int filterMaxRadius = math.max(2, (int)(verticesPerMeter * 40));
            CalculateHeightGridData(bounds, batches, heightGridResolution, filterMaxRadius, Allocator.TempJob, out var heightsMin, out var heightsMax, out var maxInstanceHeight, out var entriesPerCell);
            
            Vector3[] positions = new Vector3[meshResolutionFlat];
            Vector4[] uvs = new Vector4[meshResolutionFlat];
            Vector2[] uvs2 = new Vector2[meshResolutionFlat];

            float3 areaMin = bounds.Min;
            float3 areaMax = bounds.Max;

            Func<int2, float> calculateUCoordinate = (int2 fromCenter) =>
            {
                int d = math.max(math.abs(fromCenter.x), math.abs(fromCenter.y));
                float t = math.min(math.abs(fromCenter.x), math.abs(fromCenter.y));
                float u = d - t;
                return u;
            };

            float2 areaHorizontalMin = areaMin.xz;
            float2 areaHorizontalMax = areaMax.xz;
            if (positionToBoundsCenter)
            {
                areaHorizontalMin -= bounds.Center.xz;
                areaHorizontalMax -= bounds.Center.xz;
            }

            float vertPerMeterInv = 1.0f / verticesPerMeter;
            for (int i = 0; i < positions.Length; ++i)
            {
                int meshY = i / meshResolution.x;
                int meshX = i % meshResolution.x;
                float2 normalizedCoordinate = new float2((float)meshX / (meshResolution.x - 1), (float)meshY / (meshResolution.y - 1));

                int2 heightGridCoord = new int2(meshX / 2, meshY / 2);
                int heightGridIndex = heightGridCoord.x + heightGridCoord.y * heightGridResolution.x;

                int2 fromCenter = new int2(meshX, meshY) - meshResolution / 2;
                int d = math.max(math.abs(fromCenter.x), math.abs(fromCenter.y));
                float u  = calculateUCoordinate(fromCenter) * vertPerMeterInv;
                bool bottomVertex = d % 2 == 0;

                bool useMax = !bottomVertex;
                float localHeightMax = math.clamp(heightsMax[heightGridIndex], areaMin.y, areaMax.y);
                float localHeightMin = math.clamp(heightsMin[heightGridIndex], areaMin.y, areaMax.y);
                float localMaxInstanceHeight = maxInstanceHeight[heightGridIndex];
                localHeightMax = math.min(localHeightMax, localHeightMin + localMaxInstanceHeight);
                float height = useMax ? localHeightMax : localHeightMin;

                if (positionToBoundsCenter)
                {
                    height -= bounds.Center.y;
                    localHeightMax -= bounds.Center.y;
                    localHeightMin -= bounds.Center.y;
                }

                float x = math.lerp(areaHorizontalMin.x, areaHorizontalMax.x, normalizedCoordinate.x);
                float y = height;
                float z = math.lerp(areaHorizontalMin.y, areaHorizontalMax.y, normalizedCoordinate.y);

                positions[i] = new float3(x, y, z);
                float2 uv = normalizedCoordinate;
                uvs[i] = new float4(uv.x, uv.y, u, bottomVertex ? 0.0f : 1.0f);
                uvs2[i] = new Vector2(localHeightMin, localHeightMax);
            }

            int2 quads = new int2(meshResolution.x - 1, meshResolution.y - 1);
            int triangles = quads.x * quads.y * 2;
            int[] indices = new int[triangles * 3];
            for (int i = 0; i < triangles; ++i)
            {
                int flatQuadIndex = i / 2;
                int2 quadIndex = new int2(flatQuadIndex % quads.x, flatQuadIndex / quads.x);
                int2 centerRelativeQuadIndex = quadIndex - quads / 2;
                bool flipTrianglePattern = centerRelativeQuadIndex.x * centerRelativeQuadIndex.y > 0;
                
                int triangleInQuad = i % 2;

                int i0 = quadIndex.y * meshResolution.x + quadIndex.x;
                int i1 = quadIndex.y * meshResolution.x + quadIndex.x + 1;
                int i2 = (quadIndex.y + 1) * meshResolution.x + quadIndex.x;
                int i3 = (quadIndex.y + 1) * meshResolution.x + quadIndex.x + 1;

                if (flipTrianglePattern)
                {
                    indices[i * 3] = triangleInQuad == 0 ? i3 : i1;
                    indices[i * 3 + 1] = i0;
                    indices[i * 3 + 2] = triangleInQuad == 0 ? i2 : i3;
                }
                else
                {
                    indices[i * 3] = triangleInQuad == 0 ? i2 : i3;
                    indices[i * 3 + 1] = i1;
                    indices[i * 3 + 2] = triangleInQuad == 0 ? i0 : i2;
                }
            }

            var mesh = new Mesh();
            mesh.name = "TileImpostorMesh";
            mesh.vertices = positions;
            mesh.SetUVs(0, uvs);
            mesh.SetUVs(1, uvs2);
            mesh.indexFormat = indices.Length > 0xFFFF ? IndexFormat.UInt32 : IndexFormat.UInt16;
            mesh.SetIndices(indices, MeshTopology.Triangles, 0);

            mesh.RecalculateNormals();

            var normals = mesh.normals;
            for (int i = 0; i < normals.Length; ++i)
            {
                var n = normals[i];
                n.y = 0;
                normals[i] = n.normalized;
            }
            mesh.SetNormals(normals);
            
            mesh.RecalculateTangents();

            maxInstanceHeight.Dispose();
            heightsMax.Dispose();
            heightsMin.Dispose();
            entriesPerCell.Dispose();

            return mesh;
        }

        private struct ImpostorCardDef
        {
            public float2 horizontalPosition;
            public float2 heightMinMax;
        }
        
        public static Mesh CreateImpostorMeshScatteredCards(AABB bounds, ImpostorScatteringRenderer.Batch[] batches, int2 heightGripResolution, int maxCards, float cardWidthInMeters, bool positionToBoundsCenter)
        {
            int2 heightGridResolution = heightGripResolution;
            int filterMaxRadius = math.max(2, (int)(math.max(heightGridResolution.x, heightGridResolution.y) * 0.05f));
            CalculateHeightGridData(bounds, batches, heightGridResolution, filterMaxRadius, Allocator.TempJob, out var heightsMin, out var heightsMax, out var maxInstanceHeight, out var entriesPerCell);

            float3 areaMin = bounds.Min;
            float3 areaMax = bounds.Max;
            float2 areaHorizontalMin = areaMin.xz;
            float2 areaHorizontalMax = areaMax.xz;
            if (positionToBoundsCenter)
            {
                areaHorizontalMin -= bounds.Center.xz;
                areaHorizontalMax -= bounds.Center.xz;
            }
            
            NativeArray<SortableWeightEntry> sortedCells = new NativeArray<SortableWeightEntry>(entriesPerCell.Length, Allocator.Temp);
            for (int i = 0; i < entriesPerCell.Length; ++i)
            {
                sortedCells[i] = new SortableWeightEntry()
                {
                    index = i,
                    value = entriesPerCell[i]
                };
            }
            SortableWeightComparer comp = new SortableWeightComparer(); 
            sortedCells.Sort(comp);

            NativeArray<ImpostorCardDef> cards = new NativeArray<ImpostorCardDef>(maxCards, Allocator.Temp);
            int sortedCellsIndex = 0;
            int cardsCount = cards.Length;
            
            float2 cellExtents = bounds.Extents.xz / heightGridResolution;
            Random rand = new Random((uint)math.abs(math.dot(bounds.Center, new float3(1, 1, 1))) + 1);
            int cardsAssigned = 0;
            while(cardsAssigned < cardsCount)
            {
                int cellIndex = sortedCells[sortedCellsIndex].index;
                int numberOfEntriesInCell = sortedCells[sortedCellsIndex].value;

                //float fraction = (float)numberOfEntriesInCell / totalNumberOfEntries;
                //int numberOfCardsForCell = math.max(1, (int)(fraction * cardsCount));
                int numberOfCardsForCell = 1;//math.min(numberOfCardsForCell, cardsCount - cardsAssigned);
                
                int meshY = cellIndex / heightGridResolution.x;
                int meshX = cellIndex % heightGridResolution.x;
                float2 normalizedCoordinate = new float2((float)meshX / (heightGridResolution.x - 1), (float)meshY / (heightGridResolution.y - 1));
                
                float localHeightMax = math.clamp(heightsMax[cellIndex], areaMin.y, areaMax.y);
                float localHeightMin = math.clamp(heightsMin[cellIndex], areaMin.y, areaMax.y);
                float localMaxInstanceHeight = maxInstanceHeight[cellIndex];

                localHeightMax = math.min(localHeightMax, localHeightMin + localMaxInstanceHeight);
                
                if (positionToBoundsCenter)
                {
                    localHeightMax -= bounds.Center.y;
                    localHeightMin -= bounds.Center.y;
                }
                
                float x = math.lerp(areaHorizontalMin.x, areaHorizontalMax.x, normalizedCoordinate.x);
                float z = math.lerp(areaHorizontalMin.y, areaHorizontalMax.y, normalizedCoordinate.y);
                for (int i = 0; i < numberOfCardsForCell; ++i)
                {
                    float2 randomOffset = rand.NextFloat2(-cellExtents, cellExtents);

                    cards[cardsAssigned++] = new ImpostorCardDef()
                    {
                        horizontalPosition = new float2(x, z) + randomOffset,
                        heightMinMax = new float2(localHeightMin, localHeightMax)
                    };
                }
                

                ++sortedCellsIndex;
                if (sortedCellsIndex >= sortedCells.Length || sortedCells[sortedCellsIndex].value == 0)
                {
                    sortedCellsIndex = 0;
                }
            }

            uint[] uvs = new uint[cardsCount * 4 * 2];
            
            for (int i = 0; i < cardsCount; ++i)
            {
                ImpostorCardDef cardDef = cards[i];

                for (int vInd = 0; vInd < 4; ++vInd)
                {
                    /*float offsX = -0.5f + (vInd % 2);
                    float offsY = vInd < 2 ? cardDef.heightMinMax.x : cardDef.heightMinMax.y;

                    float x = cardDef.horizontalPosition.x + offsX * cardWidthInMeters;
                    float y = offsY;
                    float z = cardDef.horizontalPosition.y;

                    float3 p = new float3(x, y, z);
                    */
                    int vertexIndex = i * 4 + vInd;
                    float4 uv = new Vector4(cardDef.horizontalPosition.x, cardDef.horizontalPosition.y, cardDef.heightMinMax.x, cardDef.heightMinMax.y);
                    uvs[vertexIndex * 2] = math.f32tof16(uv.x) | math.f32tof16(uv.y) << 16;
                    uvs[vertexIndex * 2 + 1] = math.f32tof16(uv.z) | math.f32tof16(uv.w) << 16;
                }
            }
            
            int triangles = cardsCount * 2;
            int[] indices = new int[triangles * 3];
            for (int i = 0; i < cardsCount; ++i)
            {
                int baseIndex = i * 6;
                int baseVertex = i * 4;
                
                indices[baseIndex + 0] = baseVertex;
                indices[baseIndex + 1] = baseVertex + 1;
                indices[baseIndex + 2] = baseVertex + 2;
                
                indices[baseIndex + 3] = baseVertex + 2;
                indices[baseIndex + 4] = baseVertex + 1;
                indices[baseIndex + 5] = baseVertex + 3;
            }


            VertexAttributeDescriptor[] vertexDesc = new VertexAttributeDescriptor[1];
            vertexDesc[0] = new VertexAttributeDescriptor(VertexAttribute.TexCoord0, VertexAttributeFormat.Float16, 4);
            
            
            
            var mesh = new Mesh();
            mesh.name = "TileImpostorMeshCards";
            
            mesh.SetVertexBufferParams(uvs.Length, vertexDesc);
            mesh.SetVertexBufferData(uvs, 0, 0, uvs.Length);

            mesh.indexFormat = indices.Length > 0xFFFF ? IndexFormat.UInt32 : IndexFormat.UInt16;
            mesh.SetIndices(indices, MeshTopology.Triangles, 0);
            if (positionToBoundsCenter)
            {
                mesh.bounds = new Bounds(Vector3.zero, bounds.Size);
            }
            else
            {
                mesh.bounds = new Bounds(bounds.Center, bounds.Size);
            }
            
            
            heightsMax.Dispose();
            heightsMin.Dispose();
            maxInstanceHeight.Dispose();
            entriesPerCell.Dispose();
            sortedCells.Dispose();
            cards.Dispose();

            return mesh;
        }
        
        
        [BurstCompile]
        private unsafe struct CalculateHeight : IJobParallelFor
        {
            public AABB MeshBounds;
            [NativeDisableUnsafePtrRestriction] public Matrix4x4* Transforms;

            public AABB AreaBounds;
            public int2 HeightGridResolution;

            [NativeDisableParallelForRestriction] public NativeArray<float> HeightsMaxOutput;
            [NativeDisableParallelForRestriction] public NativeArray<float> HeightsMinOutput;
            [NativeDisableParallelForRestriction] public NativeArray<float> MaxInstanceHeight;
            [NativeDisableParallelForRestriction] public NativeArray<int> NumberOfEntriesOutput;

            public void Execute(int index)
            {
                {
                    Bounds bounds = ImpostorGeneratorUtils.TransformAABB(MeshBounds.ToBounds(), Transforms[index]);
                    float heightMax = bounds.max.y;
                    float heightMin = bounds.min.y;
                    

                    float x = bounds.center.x;
                    float z = bounds.center.z;

                    x = (x - AreaBounds.Min.x) / (AreaBounds.Max.x - AreaBounds.Min.x);
                    z = (z - AreaBounds.Min.z) / (AreaBounds.Max.z - AreaBounds.Min.z);

                    int indX = (int)math.floor(x * HeightGridResolution.x);
                    int indY = (int)math.floor(z * HeightGridResolution.y);

                    int ind = indX + indY * HeightGridResolution.x;
                    InterlockedExtension.InterlockedMax(ref ((float*)HeightsMaxOutput.GetUnsafePtr())[ind], heightMax);
                    InterlockedExtension.InterlockedMin(ref ((float*)HeightsMinOutput.GetUnsafePtr())[ind], heightMin);
                    InterlockedExtension.InterlockedMax(ref ((float*)MaxInstanceHeight.GetUnsafePtr())[ind], bounds.size.y);
                    Interlocked.Add(ref ((int*)NumberOfEntriesOutput.GetUnsafePtr())[ind], 1);
                    
                }
            }
        }

        [BurstCompile]
        private struct FillHoles : IJobParallelFor
        {
            [NativeDisableParallelForRestriction] public NativeArray<float> Source;
            public int2 HeightGridResolution;
            public int SearchRadiusPerDimension;
            public float EmptyValue;

            [NativeDisableParallelForRestriction] public NativeArray<float> Output;

            float GetSample(int x, int y)
            {
                int index = x + y * HeightGridResolution.x;
                return Source[index];
            }

            public void Execute(int index)
            {
                int indX = index % HeightGridResolution.x;
                int indY = index / HeightGridResolution.x;

                float currentValue = GetSample(indX, indY);

                if (currentValue != EmptyValue)
                {
                    Output[index] = currentValue;
                    return;
                }

                int fromX = math.max(indX - SearchRadiusPerDimension, 0);
                int fromY = math.max(indY - SearchRadiusPerDimension, 0);
                int toX = math.min(indX + SearchRadiusPerDimension, HeightGridResolution.x - 1);
                int toY = math.min(indY + SearchRadiusPerDimension, HeightGridResolution.y - 1);
                float closestDistanceSq = float.MaxValue;
                for (int x = fromX; x <= toX; ++x)
                {
                    for (int y = fromY; y <= toY; ++y)
                    {
                        float value = GetSample(x, y);
                        if (value != EmptyValue)
                        {
                            float distSq = math.lengthsq(new float2(x - indX, y - indY));
                            if (distSq < closestDistanceSq)
                            {
                                distSq = closestDistanceSq;
                                currentValue = value;
                            }
                        }
                    }
                }

                Output[index] = currentValue;
            }
        }
    }
}