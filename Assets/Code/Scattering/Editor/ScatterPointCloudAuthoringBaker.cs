using System.Collections.Generic;
using System.Threading;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;
using Unity.Entities.Serialization;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEditor;
using UnityEngine;
using Hash128 = Unity.Entities.Hash128;

namespace TimeGhost
{
    public class ScatterPointCloudAuthoringBaker : Baker<ScatterPointCloudAuthoring>
    {
        unsafe struct BlobBuilderData
        {
            public BlobBuilder mainBuilder;
            public float3* positionsPtr;
            public quaternion* orientationsPtr;
            public float* scalesPtr;
            public BlobBuilder extraDataBuilder;
            public byte* extraDataPtr;
            public int extraDataStride;
            public int maxNumberOfEntries;
        }

        public override void Bake(ScatterPointCloudAuthoring authoring)
        {
            if (authoring.pointCloudAsset == null || authoring.pointCloudAsset.GetPointCloudData() == null || authoring.pointCloudAsset.GetPointCloudData().Length == 0) return;

            Debug.Log($"ScatterPointCloudAuthoring: Prepare baking {authoring.pointCloudAsset.name}");
            
            DependsOn(authoring.pointCloudAsset);
            DependsOn(authoring.transform);
            foreach (var pc in authoring.pointCloudAsset.m_PointCaches)
            {
                if (pc != null)
                {
                    DependsOn(pc);
                }
            }

            int totalNumberOfScatteredInstances = 0;
            var mainEntity = GetEntity(TransformUsageFlags.None);

            //preload the prefabs ASAP (copying them later will take considerable longer than when the world is almost empty)
            {
                NativeHashSet<EntityPrefabReference> prefabSet = new NativeHashSet<EntityPrefabReference>(64, Allocator.Temp);

                foreach (var pc in authoring.pointCloudAsset.GetPointCloudData())
                {
                    if (pc.prefab == null)
                        continue;
                    DependsOn(pc.prefab);
                    prefabSet.Add(new EntityPrefabReference(pc.prefab));
                }

                var allPrefabsArray = prefabSet.ToNativeArray(Allocator.Temp);
                AddBuffer<ScatterPointCloudPreloadPrefab>(mainEntity).CopyFrom(allPrefabsArray.Reinterpret<ScatterPointCloudPreloadPrefab>());
                prefabSet.Dispose();
                allPrefabsArray.Dispose();
            }

            float4x4 transformMat = authoring.transform.localToWorldMatrix;

            List<PointCloudUtility.PointCloudDataEntry> dataList = new List<PointCloudUtility.PointCloudDataEntry>();
            {
                foreach (var pc in authoring.pointCloudAsset.GetPointCloudData())
                {

                    if (pc.positions == null || pc.positions.Length == 0 || pc.prefab == null)
                        continue;

                    dataList.Add(new PointCloudUtility.PointCloudDataEntry{transform = transformMat, pcData = pc});
                }
                
            }

            float tileSize = math.max(authoring.scatterSceneSectionSize, authoring.scatterTileSize);
            var pointCloudDataArray = dataList.ToArray();
            var partitionOutput = PointCloudUtility.PartitionPointCloudDataToTiles(pointCloudDataArray, tileSize, Allocator.TempJob);
            partitionOutput.pendingJobs.Complete();
            // If we only had a single point we'll end up with zero-sized bounding box. This breaks further down
            // the processing chain  when we try to calculate cell counts from bounds size.
            if (math.all(partitionOutput.partitioningInfo.bounds.Min == partitionOutput.partitioningInfo.bounds.Max))
            {
                partitionOutput.partitioningInfo.bounds.Extents += 1e-2f; // can't go too small since this will mix with large values
            }

            
            // NOTE: This means we're effectively hashing all data twice, but it's probably not too much of a concern since it's at bake time.
            var groupId = CommonScatterUtilities.CalculateGroupPointCloudIdentifier(authoring);

            for(uint pcIndex = 0, pcCount = (uint)pointCloudDataArray.Length, entityIndex = 0; pcIndex < pcCount; ++pcIndex)
            {
                if (CommonScatterUtilities.CalculatePointCloudIdentifier(authoring, pcIndex, entityIndex, out var id))
                {
                    var pc = pointCloudDataArray[pcIndex].pcData;
                    totalNumberOfScatteredInstances += pc.positions.Length;
                    CreateScatteringData(mainEntity, ref partitionOutput, (int)pcIndex, pc, groupId, id, authoring.scatterTileSize, authoring.pointCloudAsset.m_IgnoreMaxScatterDistance);
                    ++entityIndex;
                }
            }

            float BOUNDS_MARGIN_CELL_SIZE_RATIO = 0.001f;
            var partitioningBounds = partitionOutput.partitioningInfo.bounds;
            float extraMargin = partitionOutput.partitioningInfo.cellSize * BOUNDS_MARGIN_CELL_SIZE_RATIO;
            partitioningBounds.Extents += extraMargin;
            
            AddComponent(mainEntity, new ScatteredInstanceSpatialPartitioningAreaBounds() { Bounds = partitionOutput.partitioningInfo.bounds });
            AddComponent(mainEntity, new ScatterPointCloudBakingSystem.PointCloudGroupActivationDistances() {ActiveAreaMin = authoring.scatterActiveDistanceMin, ActiveAreaMax = authoring.scatterActiveDistanceMax});
        
            Debug.Log($"ScatterPointCloudAuthoring: Bake initiated for {totalNumberOfScatteredInstances} instances");
            partitionOutput.Dispose();
        }
        
        private void EnsureScatteringPrefabComponents(GameObject prefab)
        {
            if (!prefab.TryGetComponent(out ScatteringPrefab sp))
            {
                prefab.AddComponent<ScatteringPrefab>();
                AssetDatabase.SaveAssetIfDirty(prefab);
            }
        }

        private void CreateScatteringData(Entity mainEntity, ref PointCloudUtility.PointCloudPartitionOutput partitioning, int pointCloudIndexInPartitioning, PointCloudFromHoudiniAsset.PointCloudData pointCloud, Hash128 groupIdentifier, Hash128 identifier, float smallTileSize, bool ignoreScatterMaxDistance)
        {
            BuildAttributesBlobsForTiles(ref partitioning, pointCloudIndexInPartitioning, pointCloud.rotations, pointCloud.scales, pointCloud.age, pointCloud.health, pointCloud.color, pointCloud.partIndices, out var attributesList, out var extraDataList, out var partitioningIndices);
            
            var prefab = pointCloud.prefab;

            AABB prefabBounds = GetBoundsForPrefab(prefab);
            
            int numberOfSections = attributesList.Length;
            if (numberOfSections != 0)
            {
                
                //EnsureScatteringPrefabComponents(prefab);
                var entityPrefabRef = new EntityPrefabReference(prefab);
                NativeArray<Entity> newEntities = new NativeArray<Entity>(numberOfSections, Allocator.Temp, NativeArrayOptions.UninitializedMemory);
                CreateAdditionalEntities(newEntities, TransformUsageFlags.None);

                for (int i = 0; i < numberOfSections; ++i)
                {

                    Entity entity = newEntities[i];
                    int partitioningIndex = partitioningIndices[i];
                    AABB partitioningBounds = partitioning.partitioningInfo.GetCellBounds(partitioningIndex);
                    BlobAssetReference<ScatterPointCloudInstanceAttributes> attribs = attributesList[i];
                    BlobAssetReference<ScatteringExtraDataBlob> extraDataBlobRef = extraDataList[i];

                    int extraDataMask = extraDataBlobRef.IsCreated ? extraDataBlobRef.Value.ExtraDataMask : 0;
                    
                    UnityEngine.Hash128 scatterId = identifier;
                    scatterId.Append(ref partitioningBounds);

                    int sceneIndex = 1 + partitioningIndex;
                    
                    AddComponent(entity, new ScatterPointCloudInstanceData() { Attributes = attribs, ScatterId = scatterId, ScatterGroupId = groupIdentifier, PointCloudBounds = partitioningBounds, CellSizeInMeters = smallTileSize, ExtraDataMask = extraDataMask});
                    AddComponent(entity, new ScatteredPointCloudNeedsScatterTag());
                    AddSharedComponent(entity, new ScatterPointCloudScatterId() { ScatterId = scatterId });
                    AddSharedComponent(entity, new ScatterPointCloudScatterGroupId() { Value = groupIdentifier });
                    AddComponent(entity, new ScatterPointCloudPrefab() { PrefabRef = entityPrefabRef });
                    
                    AddComponent<ScatterPointCloudBakingSystem.RequiresSpatialPartitioningTag>(entity); //request spatial partioning of point cloud data
                    AddComponent<ScatterPointCloudBakingSystem.CalculatePointCloudPointImportanceTag>(entity); //calculate density for points
                    
                    AddComponent<ScatterPointCloudBakingSystem.AssignSceneSectionTag>(entity);
                    AddSharedComponent(entity, new ScatterPointCloudBakingSystem.AuthoringBatchReference { MainEntity = mainEntity });
                    AddSharedComponent(entity, new SceneSection { SceneGUID = GetSceneGUID(), Section = sceneIndex }); //section index is modified later in ScatterPointCloudBakingSystem where all baker outputs are visible
                    AddComponent(entity, new ScatterPointCloudBakingSystem.PointCloudPrefabSize(){ PrefabSize = prefabBounds });
                    
                    if (extraDataBlobRef.IsCreated)
                    {
                        AddComponent(entity, new ScatteringExtraData() { ExtraData = extraDataBlobRef, ExtraDataHash = scatterId });
                    }

                    if (ignoreScatterMaxDistance)
                    {
                        AddComponent<IgnoreMaxScatterDistanceTag>(entity);
                    }

                }


                newEntities.Dispose();
            }

            extraDataList.Dispose();
            attributesList.Dispose();
            partitioningIndices.Dispose();
        }

        //TODO: parallelize
        void BuildAttributesBlobsForTiles(
            ref PointCloudUtility.PointCloudPartitionOutput partitioning, int pointCloudDataIndex,
            float4[] rotations, float[] scales,
            float[] age, float[] health, Color32[] color, uint[] partIndices,
            out NativeArray<BlobAssetReference<ScatterPointCloudInstanceAttributes>> attributesOut,
            out NativeArray<BlobAssetReference<ScatteringExtraDataBlob>> extraDataOut, 
            out NativeArray<int> partitioningIndicesOut)
        {
            
            //calculate number of tiles and points per tile and reserve resources
            int pointCount = rotations.Length;
            int numberOfTiles = partitioning.partitioningInfo.GetNumberOfCells().x * partitioning.partitioningInfo.GetNumberOfCells().y;
            NativeArray<BlobBuilderData> blobBuilders = new NativeArray<BlobBuilderData>(numberOfTiles, Allocator.Temp, NativeArrayOptions.UninitializedMemory);

            PointCloudFromHoudini.CreateExtraData(pointCount, age, health, color, partIndices, out var extraDataArray, out var mask);
            
            int extraDataStride = ExtraDataUtils.GetExtraDataStride(mask);
            int numberOfNonZeroTiles = 0;
            unsafe
            {
                for (int i = 0; i != numberOfTiles; ++i)
                {
                    int ind = pointCloudDataIndex * numberOfTiles + i;
                    int2 offsetAndCountPerCell = partitioning.pointCloudEntryOffsetAndCountPerCell[ind];
                    
                    if (offsetAndCountPerCell.y > 0)
                    {
                        int pointsInTile = offsetAndCountPerCell.y;
                        var mainBuilder = new BlobBuilder(Allocator.Temp);
                        ref ScatterPointCloudInstanceAttributes attr =
                            ref mainBuilder.ConstructRoot<ScatterPointCloudInstanceAttributes>();
                        BlobBuilderArray<float3> positionsBuilder = mainBuilder.Allocate(ref attr.Positions, pointsInTile);
                        BlobBuilderArray<quaternion> orientationsBuilder = mainBuilder.Allocate(ref attr.Orientations, pointsInTile);
                        BlobBuilderArray<float> scalesBuilder = mainBuilder.Allocate(ref attr.Scales, pointsInTile);

                        byte* extraDataPtr = null;
                        BlobBuilder extraDataBuilder = default;

                        if (mask != 0)
                        {
                            extraDataBuilder = new BlobBuilder(Allocator.Temp);
                            ref ScatteringExtraDataBlob extraExtraDataBlob =
                                ref extraDataBuilder.ConstructRoot<ScatteringExtraDataBlob>();
                            extraExtraDataBlob.ExtraDataMask = mask;
                            BlobBuilderArray<byte> extraDataBlobArray = extraDataBuilder.Allocate(ref extraExtraDataBlob.Data, pointsInTile * extraDataStride);
                            extraDataPtr = (byte*)extraDataBlobArray.GetUnsafePtr();
                        }


                        BlobBuilderData builderData = new BlobBuilderData()
                        {
                            mainBuilder = mainBuilder,
                            positionsPtr = (float3*)positionsBuilder.GetUnsafePtr(),
                            orientationsPtr = (quaternion*)orientationsBuilder.GetUnsafePtr(),
                            scalesPtr = (float*)scalesBuilder.GetUnsafePtr(),
                            extraDataPtr = extraDataPtr,
                            extraDataBuilder = extraDataBuilder,
                            extraDataStride = extraDataStride,
                            maxNumberOfEntries = pointsInTile,
                        };
                        blobBuilders[i] = builderData;
                        ++numberOfNonZeroTiles;
                    }
                    else
                    {
                        BlobBuilderData builderData = new BlobBuilderData()
                        {
                            maxNumberOfEntries = 0
                        };
                        blobBuilders[i] = builderData;
                    }
                }

                JobHandle jobHandle = default;
                
                fixed(float4* orientationsPtr = rotations)
                fixed(float* scalesPtr = scales)
                {
                    for (int cellIndex = 0; cellIndex < numberOfTiles; ++cellIndex)
                    {
                        int ind = pointCloudDataIndex * numberOfTiles + cellIndex;
                        int2 offsetAndCountPerCell = partitioning.pointCloudEntryOffsetAndCountPerCell[ind];
                        BlobBuilderData blobBuilderData = blobBuilders[cellIndex];

                        var jh = new FillDataForCell()
                        {
                            PositionsOutPtr = blobBuilderData.positionsPtr,
                            OrientationsOutPtr = blobBuilderData.orientationsPtr,
                            ScalesOutPtr = blobBuilderData.scalesPtr,
                            ExtraDataOutPtr = blobBuilderData.extraDataPtr,

                            GlobalPositionsPtr = (float3*)partitioning.transformedPositions.GetUnsafePtr(),
                            RotationsPtr = orientationsPtr,
                            ScalesPtr = scalesPtr,
                            ExtraDataPtr = (byte*)extraDataArray.GetUnsafePtr(),
                            
                            ToPointCloudDataEntryMapping = partitioning.toPointCloudDataEntryMapping,
                            IndicesPerCell = partitioning.cellToIndexMapping,
                        
                            IndicesArrayOffsetCount = offsetAndCountPerCell,
                            ExtraDataStride = blobBuilderData.extraDataStride
                        }.Schedule();

                        jobHandle = JobHandle.CombineDependencies(jobHandle, jh);
                    }
                }
                
                jobHandle.Complete();

            }


            //fill outputs
            NativeArray<BlobAssetReference<ScatterPointCloudInstanceAttributes>> attributes = new NativeArray<BlobAssetReference<ScatterPointCloudInstanceAttributes>>(numberOfNonZeroTiles, Allocator.Temp, NativeArrayOptions.UninitializedMemory);
            NativeArray<BlobAssetReference<ScatteringExtraDataBlob>> extraDataBlobs = new NativeArray<BlobAssetReference<ScatteringExtraDataBlob>>(numberOfNonZeroTiles, Allocator.Temp, NativeArrayOptions.UninitializedMemory);
            NativeArray<int> partitioningIndices = new NativeArray<int>(numberOfNonZeroTiles, Allocator.Temp, NativeArrayOptions.UninitializedMemory);

            int outputIndex = 0;
            for (int i = 0; i < blobBuilders.Length; ++i)
            {
                if (blobBuilders[i].maxNumberOfEntries == 0) continue;


                attributes[outputIndex] = blobBuilders[i].mainBuilder.CreateBlobAssetReference<ScatterPointCloudInstanceAttributes>(Allocator.Persistent);
                partitioningIndices[outputIndex] = i;
                blobBuilders[i].mainBuilder.Dispose();
                if (blobBuilders[i].extraDataBuilder.IsCreated)
                {
                    extraDataBlobs[outputIndex] = blobBuilders[i].extraDataBuilder
                        .CreateBlobAssetReference<ScatteringExtraDataBlob>(Allocator.Persistent);
                    blobBuilders[i].extraDataBuilder.Dispose();
                }

                ++outputIndex;
            }

            blobBuilders.Dispose();
            extraDataArray.Dispose();

            attributesOut = attributes;
            extraDataOut = extraDataBlobs;
            partitioningIndicesOut = partitioningIndices;
        }

        [BurstCompile]
        private unsafe struct FillDataForCell : IJob
        {
            [NativeDisableUnsafePtrRestriction][ReadOnly]
            public float3* GlobalPositionsPtr;
            [NativeDisableUnsafePtrRestriction][ReadOnly]
            public float4* RotationsPtr;
            [NativeDisableUnsafePtrRestriction][ReadOnly]
            public float* ScalesPtr;
            [NativeDisableUnsafePtrRestriction][ReadOnly]
            public byte* ExtraDataPtr;
            
            [NativeDisableUnsafePtrRestriction]
            public float3* PositionsOutPtr;
            [NativeDisableUnsafePtrRestriction]
            public quaternion* OrientationsOutPtr;
            [NativeDisableUnsafePtrRestriction]
            public float* ScalesOutPtr;
            [NativeDisableUnsafePtrRestriction]
            public byte* ExtraDataOutPtr;
            
            [ReadOnly] 
            public NativeArray<int2> ToPointCloudDataEntryMapping;
            [ReadOnly]
            public NativeArray<int> IndicesPerCell;

            public int2 IndicesArrayOffsetCount;
            public int ExtraDataStride;
            public void Execute()
            {
                int2 indicesArrayOffsetCount = IndicesArrayOffsetCount;

                if (indicesArrayOffsetCount.y == 0) return;

                for (int i = 0; i < indicesArrayOffsetCount.y; ++i)
                {
                    int globalIndex = IndicesPerCell[indicesArrayOffsetCount.x + i];
                    int2 pcRelativeMapping = ToPointCloudDataEntryMapping[globalIndex];
                    int indexDst = i;
                    int indexSrc = pcRelativeMapping.y;
                    
                    PositionsOutPtr[indexDst] = GlobalPositionsPtr[globalIndex]; //positions are a special case since they are drawn from "global" buffer
                    OrientationsOutPtr[indexDst] = RotationsPtr[indexSrc];
                    ScalesOutPtr[indexDst] = ScalesPtr[indexSrc];

                    if (ExtraDataPtr != null && ExtraDataStride != 0)
                    {
                        UnsafeUtility.MemCpy(ExtraDataOutPtr + indexDst * ExtraDataStride,  ExtraDataPtr+ indexSrc * ExtraDataStride, ExtraDataStride);
                    }

                }
            }
        }
        
        AABB GetBoundsForPrefab(GameObject prefab)
        {
            {
                Bounds bounds = new Bounds();
                bool boundsInitialized = false;
                if (prefab.TryGetComponent(out LODGroup grp))
                {
                    LOD[] lods = grp.GetLODs();
                    foreach (var lod in lods)
                    {
                        var renderers = lod.renderers;
                        foreach (var rend in renderers)
                        {
                            if (boundsInitialized)
                            {
                                bounds.Encapsulate(rend.localBounds);
                            }
                            else
                            {
                                boundsInitialized = true;
                                bounds = rend.localBounds;
                            }

                        }
                    }
                }
                else
                {
                    if (prefab.TryGetComponent(out MeshRenderer mr))
                    {
                        boundsInitialized = true;
                        bounds = mr.localBounds;
                    }
                }

                if (!boundsInitialized)
                {
                    Debug.LogError($"Failed to find bounds for {prefab.name}");
                    bounds = new Bounds(Vector3.zero, Vector3.zero);
                }
                
                bounds.extents = Vector3.Scale(bounds.extents, prefab.transform.localScale);

                return bounds.ToAABB();
            }
        }
    }
}
