using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using Unity.Rendering;
using Unity.Transforms;
#if HAS_ENVIRONMENT
    using Unity.Environment;
#endif

namespace TimeGhost
{
    //system components
    internal struct ScatteredInstanceSpatialPartitioningSettingsData : IComponentData
    {
        public float CellSizeInMeters;
    }
    
    internal unsafe struct ScatteredInstanceSpatialPartitioningData: IComponentData
    {
        public float CellSizeInMeters;
        public AABB CanvasArea;
        //per tile arrays
        public NativeArray<int> InstancesPerTile;
        public NativeArray<UnsafeList<int>> PerTileReservedPages;
        public NativeArray<int> TileRevisionNumber;
        public NativeArray<int> FreePages;
        //actual pages data
        public int* ReservedPagesCounter;
        public int* FreePagesCounter;
        public NativeReference<UnsafeList<ScatteredInstancePropertiesPacked>> InstanceDataPages;
        public NativeReference<UnsafeList<Entity>> EntityHandlePerDataEntry; //Entity per ScatteredInstanceArrayData. Used just to remove entries

        public uint2 CalculateGridResolution()
        {
            float2 res = math.ceil(CanvasArea.Size.xz / CellSizeInMeters);
            return (uint2)res;
        }
        
    }
    
    //scattered instance components
    internal struct ScatteredInstancePartitioningTag : IComponentData
    {
    }
    
    internal struct ScatteredInstanceNeedsPartitioningTag : IComponentData
    {
    }

    internal struct ScatteredInstanceReadyTag : IComponentData
    {
    }

    internal struct ScatteredInstancePartitioningData : ICleanupComponentData
    {
        public int FlatTileIndex;
        public int IndexInTile;
    }

    internal struct ScatteredInstanceSpringData : ISharedComponentData
    {
        public float3 SpringTipOffset;
        public float SpringTipRadius;
        public float2 DampingMinMax;
        public float2 StiffnessMinMax;
        public float2 BreakingAngleMinMax;
        public float2 BreakingRecoveryAngleMinMax;
    }
    
    
    public struct ScatteredInstanceSpatialPartitioningAreaBounds : IComponentData
    {
        public AABB Bounds;
    }
    
    public struct ScatteredInstanceSpatialPartitioningAreaBoundsProcessedTag : ICleanupComponentData
    {
    }
    
    //Gather MeshLodComponent children.
    [InternalBufferCapacity(4)]
    internal struct ScatteredInstanceChildren : IBufferElementData
    {
        public Entity Child;
    }
    
    [MaterialProperty("_InteractiveState")]
    internal struct ScatteredInstanceRenderTileData : IComponentData
    {
        //float for now, since the SG only accepts floats. TODO: see if it does a conversion or just blindly writes the values, implying maybe we could just cast
        public float2 TileIndices; //x == tile index, y == index in a tile
    }
    

    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateBefore(typeof(EndSimulationEntityCommandBufferSystem))]
    [WorldSystemFilter(WorldSystemFilterFlags.Default | WorldSystemFilterFlags.Editor)]

    public partial struct ScatteredInstanceSpatialPartitioningSystem : ISystem
    {
        public static readonly int INSTANCE_DATA_PAGE_SIZE = 512;
        
        //partitioning queries
        private EntityQuery m_RemovedEntitiesQuery;
        private EntityQuery m_NonInitializedEntityQuery;
        private EntityQuery m_NonPartitionedInstancesQuery;
        private EntityQuery m_AllNonRemovedEntities;

        private EntityQuery m_NewBoundsQuery;
        private EntityQuery m_AlreadySeenBoundsQuery;
        private EntityQuery m_RemovedBoundsQuery;

        //misc
        private EntityTypeHandle m_EntityTypeHandle;
        private ComponentTypeHandle<LocalToWorld> m_TransformType;
        private ComponentTypeHandle<ScatteredInstancePartitioningData> m_PartitionedDataType;
        private ComponentTypeHandle<ScatteredInstancePartitioningData> m_PartitionedDataTypeWrite;
        private SharedComponentTypeHandle<ScatteredInstanceSpringData> m_PhysicsParametersType;
        private BufferLookup<ScatteredInstanceChildren> m_ChildBufferLookup;
        
        private ComponentLookup<ScatteredInstancePartitioningData> m_PartitionedDataLookup;
        private ComponentLookup<ScatteredInstanceRenderTileData> m_RenderDataLookup;
        private BufferLookup<ScatteredInstanceChildren> m_InstanceChildrenArrayLookup;

        private MinMaxAABB m_CurrentBounds;
        
        private static readonly int INITIAL_DATA_PAGES_COUNT = 1024;
        private static readonly float DATA_PAGES_GROW_STRATEGY = 1.5f;

 
 #region SystemCalls

        public void OnCreate(ref SystemState state) 
        {
            state.RequireForUpdate<EndSimulationEntityCommandBufferSystem.Singleton>();

            m_NonInitializedEntityQuery = state.EntityManager.CreateEntityQuery(
                ComponentType.ReadOnly<ScatteredInstancePartitioningData>(),
                ComponentType.Exclude<ScatteredInstancePartitioningTag>(), ComponentType.Exclude<Prefab>(),
                ComponentType.ReadOnly<ScatteredInstanceNeedsPartitioningTag>());
            
            m_NonPartitionedInstancesQuery = state.EntityManager.CreateEntityQuery(
                ComponentType.ReadOnly<ScatteredInstancePartitioningTag>(), ComponentType.ReadWrite<ScatteredInstancePartitioningData>(),
                ComponentType.ReadOnly<LocalToWorld>(), ComponentType.ReadOnly<ScatteredInstanceSpringData>(),
                ComponentType.Exclude<ScatteredInstanceReadyTag>(), ComponentType.Exclude<Prefab>(),
                ComponentType.ReadOnly<ScatteredInstanceNeedsPartitioningTag>());
            
            m_RemovedEntitiesQuery = state.EntityManager.CreateEntityQuery(typeof(ScatteredInstancePartitioningData), ComponentType.Exclude<ScatteredInstanceNeedsPartitioningTag>());
            m_AllNonRemovedEntities = state.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<ScatteredInstancePartitioningTag>(), ComponentType.Exclude<Prefab>());

            m_NewBoundsQuery = state.EntityManager.CreateEntityQuery(
                ComponentType.ReadOnly<ScatteredInstanceSpatialPartitioningAreaBounds>()
                , ComponentType.Exclude<ScatteredInstanceSpatialPartitioningAreaBoundsProcessedTag>());
            m_AlreadySeenBoundsQuery = state.EntityManager.CreateEntityQuery(
                ComponentType.ReadOnly<ScatteredInstanceSpatialPartitioningAreaBounds>()
                , ComponentType.ReadOnly<ScatteredInstanceSpatialPartitioningAreaBoundsProcessedTag>());
            m_RemovedBoundsQuery = state.EntityManager.CreateEntityQuery(
                ComponentType.Exclude<ScatteredInstanceSpatialPartitioningAreaBounds>()
                , ComponentType.ReadOnly<ScatteredInstanceSpatialPartitioningAreaBoundsProcessedTag>());
            
            m_EntityTypeHandle = SystemAPI.GetEntityTypeHandle();
            m_TransformType = SystemAPI.GetComponentTypeHandle<LocalToWorld>(true);
            m_PartitionedDataType = SystemAPI.GetComponentTypeHandle<ScatteredInstancePartitioningData>(true);
            m_PartitionedDataTypeWrite = SystemAPI.GetComponentTypeHandle<ScatteredInstancePartitioningData>();
            m_PhysicsParametersType = SystemAPI.GetSharedComponentTypeHandle<ScatteredInstanceSpringData>();
            m_ChildBufferLookup = SystemAPI.GetBufferLookup<ScatteredInstanceChildren>();
            
            
            m_PartitionedDataLookup = SystemAPI.GetComponentLookup<ScatteredInstancePartitioningData>();
            m_RenderDataLookup = SystemAPI.GetComponentLookup<ScatteredInstanceRenderTileData>();
            m_InstanceChildrenArrayLookup = SystemAPI.GetBufferLookup<ScatteredInstanceChildren>();

            state.EntityManager.AddComponent<ScatteredInstanceSpatialPartitioningSettingsData>(state.SystemHandle);
            state.EntityManager.AddComponent<ScatteredInstanceSpatialPartitioningData>(state.SystemHandle);
            
            var settingsData = state.EntityManager.GetComponentData<ScatteredInstanceSpatialPartitioningSettingsData>(state.SystemHandle);
            settingsData.CellSizeInMeters = 10;
            var partitioningData = state.EntityManager.GetComponentData<ScatteredInstanceSpatialPartitioningData>(state.SystemHandle);
            
            //create resources that don't need to be recreated on reset
            unsafe
            {
                partitioningData.ReservedPagesCounter = (int*)UnsafeUtility.Malloc(sizeof(int), 1, Allocator.Persistent);
                partitioningData.FreePagesCounter = (int*)UnsafeUtility.Malloc(sizeof(int), 1, Allocator.Persistent);
            }
            partitioningData.EntityHandlePerDataEntry = new NativeReference<UnsafeList<Entity>>(Allocator.Persistent);
            partitioningData.EntityHandlePerDataEntry.Value = new UnsafeList<Entity>(INITIAL_DATA_PAGES_COUNT * INSTANCE_DATA_PAGE_SIZE, Allocator.Persistent);
            partitioningData.InstanceDataPages = new NativeReference<UnsafeList<ScatteredInstancePropertiesPacked>>(Allocator.Persistent);
            partitioningData.InstanceDataPages.Value = new UnsafeList<ScatteredInstancePropertiesPacked>(INITIAL_DATA_PAGES_COUNT * INSTANCE_DATA_PAGE_SIZE, Allocator.Persistent);
            partitioningData.FreePages = new NativeArray<int>(INITIAL_DATA_PAGES_COUNT, Allocator.Persistent);
            
            state.EntityManager.SetComponentData(state.SystemHandle, partitioningData);
            state.EntityManager.SetComponentData(state.SystemHandle, settingsData);

            m_CurrentBounds = MinMaxAABB.Empty;

        }

        public void ResetComponents(ref SystemState state)
        {
            state.EntityManager.RemoveComponent<ScatteredInstanceReadyTag>(m_AllNonRemovedEntities);
        }
        [BurstCompile]
        public void OnDestroy(ref SystemState state)
        {
            ResetComponents(ref state);

            m_NonInitializedEntityQuery.Dispose();
            m_NonPartitionedInstancesQuery.Dispose();
            m_RemovedEntitiesQuery.Dispose();
            m_AllNonRemovedEntities.Dispose();
            
            m_NewBoundsQuery.Dispose();
            m_AlreadySeenBoundsQuery.Dispose();
            m_RemovedBoundsQuery.Dispose();
            
            
            var partitioningData = state.EntityManager.GetComponentData<ScatteredInstanceSpatialPartitioningData>(state.SystemHandle);
            partitioningData.InstancesPerTile.Dispose();
            partitioningData.TileRevisionNumber.Dispose();

            partitioningData.InstanceDataPages.Value.Dispose();
            partitioningData.InstanceDataPages.Dispose();
            partitioningData.EntityHandlePerDataEntry.Value.Dispose();
            partitioningData.EntityHandlePerDataEntry.Dispose();

            if (partitioningData.PerTileReservedPages.IsCreated)
            {
                foreach (var perTileArray in partitioningData.PerTileReservedPages)
                {
                    perTileArray.Dispose();
                }

                partitioningData.PerTileReservedPages.Dispose();
            }
            partitioningData.FreePages.Dispose();
            
            unsafe
            {
                UnsafeUtility.Free(partitioningData.ReservedPagesCounter, Allocator.Persistent);
                UnsafeUtility.Free(partitioningData.FreePagesCounter, Allocator.Persistent);
            }
        }
        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            var partitioningData = state.EntityManager.GetComponentData<ScatteredInstanceSpatialPartitioningData>(state.SystemHandle);
            var settingsData = state.EntityManager.GetComponentData<ScatteredInstanceSpatialPartitioningSettingsData>(state.SystemHandle);

            bool boundsWereReset = UpdateBounds(ref state);
            {
                var currentCanvasAAbb = (AABB)m_CurrentBounds;
                var previousCanvasAABB = partitioningData.CanvasArea;
                
                //some sanity check for settings
                if (settingsData.CellSizeInMeters == 0 || m_CurrentBounds.IsEmpty ) return;
                
                if (partitioningData.CellSizeInMeters != settingsData.CellSizeInMeters || math.any(currentCanvasAAbb.Center != previousCanvasAABB.Center) || math.any(currentCanvasAAbb.Extents != previousCanvasAABB.Extents))
                {
                    partitioningData.CanvasArea = currentCanvasAAbb;
                    partitioningData.CellSizeInMeters = settingsData.CellSizeInMeters;
                    Reset(ref state, ref partitioningData);

                }
            }
            
            m_EntityTypeHandle.Update(ref state);
            m_TransformType.Update(ref state);
            m_PhysicsParametersType.Update(ref state);
            m_PartitionedDataType.Update(ref state);
            m_PartitionedDataTypeWrite.Update(ref state);
            m_ChildBufferLookup.Update(ref state);
            m_RenderDataLookup.Update(ref state);
            m_InstanceChildrenArrayLookup.Update(ref state);
            m_PartitionedDataLookup.Update(ref state);
            
            
            var lastDependency = state.Dependency;
            var ecb = SystemAPI.GetSingleton<EndSimulationEntityCommandBufferSystem.Singleton>().CreateCommandBuffer(state.WorldUnmanaged);

            var tempPerTileChangedFlag = new NativeArray<int>(partitioningData.InstancesPerTile.Length, Allocator.TempJob);
            if (!boundsWereReset) //if bounds were reset, no need to cleanup removed entities since the partitioning will be fully refreshed
            {
                lastDependency = RemoveInstancesFromPartitioning(ref state, lastDependency, ref partitioningData, tempPerTileChangedFlag);
            }
            
            lastDependency = AddNewInstanceToPartitioning(ref state, lastDependency, ref partitioningData, tempPerTileChangedFlag);
            lastDependency = UpdateTileRevisionNumber(ref state, lastDependency, ref partitioningData, tempPerTileChangedFlag);
            tempPerTileChangedFlag.Dispose(lastDependency);
            
            lastDependency = ReserveRequiredPages(ref state, lastDependency, ref partitioningData);
            lastDependency = EnsureEnoughDataStorage(ref state, lastDependency, ref partitioningData);
            lastDependency = FillScatteredInstancePartitioningData(ref state, lastDependency, ref partitioningData);
            
            
            
            ecb.AddComponent<ScatteredInstanceReadyTag>(m_NonPartitionedInstancesQuery, EntityQueryCaptureMode.AtPlayback); // tag instances that were added to partitioning
            ecb.AddComponent<ScatteredInstancePartitioningTag>(m_NonInitializedEntityQuery, EntityQueryCaptureMode.AtPlayback); // for uninitialized instances, add ScatteredInstancePartitioningTag (will be handled next frame)
            ecb.RemoveComponent<ScatteredInstancePartitioningData>(m_RemovedEntitiesQuery, EntityQueryCaptureMode.AtPlayback); // remove instance partitioning data from deleted entitites
            
            state.EntityManager.SetComponentData(state.SystemHandle, partitioningData);

            state.Dependency = lastDependency;
        }
#endregion

        [BurstCompile]
        public bool UpdateBounds(ref SystemState state)
        {
            //handle removed bounds
            bool boundsNeedReset = false;
            if(!m_RemovedBoundsQuery.IsEmpty)
            {
                boundsNeedReset = true;
                state.EntityManager.RemoveComponent<ScatteredInstanceSpatialPartitioningAreaBoundsProcessedTag>(
                    m_RemovedBoundsQuery);
            }

            if (boundsNeedReset)
            {
                m_CurrentBounds = MinMaxAABB.Empty;
                var oldBounds = m_AlreadySeenBoundsQuery.ToComponentDataArray<ScatteredInstanceSpatialPartitioningAreaBounds>(Allocator.Temp);
                foreach (var b in oldBounds)
                {
                    m_CurrentBounds.Encapsulate(b.Bounds);
                }
                oldBounds.Dispose();
            }
            
            bool newBoundsFound = false;
            {
                var newBounds = m_NewBoundsQuery.ToComponentDataArray<ScatteredInstanceSpatialPartitioningAreaBounds>(Allocator.Temp);
                if (newBounds.Length > 0)
                {
                    newBoundsFound = true;
                }
                foreach (var b in newBounds)
                {
                    m_CurrentBounds.Encapsulate(b.Bounds);
                }
                newBounds.Dispose();

                state.EntityManager.AddComponent<ScatteredInstanceSpatialPartitioningAreaBoundsProcessedTag>(
                    m_NewBoundsQuery);
            }

            return boundsNeedReset || newBoundsFound;
        }
        [BurstCompile]
        private void Reset(ref SystemState state, ref ScatteredInstanceSpatialPartitioningData partitioningData)
        {
            if (partitioningData.InstancesPerTile.IsCreated)
            {
                partitioningData.InstancesPerTile.Dispose();
            }

            if (partitioningData.TileRevisionNumber.IsCreated)
            {
                partitioningData.TileRevisionNumber.Dispose();
            }

            if (partitioningData.PerTileReservedPages.IsCreated)
            {
                foreach (var perTileArray in partitioningData.PerTileReservedPages)
                {
                    perTileArray.Dispose();
                }

                partitioningData.PerTileReservedPages.Dispose();
            }

            int2 tileRes = (int2)partitioningData.CalculateGridResolution();
            partitioningData.InstancesPerTile = new NativeArray<int>(tileRes.x * tileRes.y, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            partitioningData.TileRevisionNumber = new NativeArray<int>(tileRes.x * tileRes.y, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            partitioningData.PerTileReservedPages = new NativeArray<UnsafeList<int>>(tileRes.x * tileRes.y, Allocator.Persistent);
   
            unsafe
            {
                UnsafeUtility.MemSet(partitioningData.InstancesPerTile.GetUnsafePtr(), 0, sizeof(int) * partitioningData.InstancesPerTile.Length);
                UnsafeUtility.MemSet(partitioningData.TileRevisionNumber.GetUnsafePtr(), 0, sizeof(int) * partitioningData.TileRevisionNumber.Length);
            }

            unsafe
            {
                *partitioningData.ReservedPagesCounter = 0;
                *partitioningData.FreePagesCounter = 0;
            }



            ResetComponents(ref state);
        }
        
        [BurstCompile]
        static void CalculateTileIndex(ref float2 worldPos, ref float2 worldCorner, ref uint2 gridResolution, float cellSizeInv, out int2 tileIndex)
        {

            float2 tileF = math.floor((worldPos - worldCorner) * cellSizeInv);
            int2 tile = new int2((int)tileF.x, (int)tileF.y);
            
            bool invalidIndex = tile.x < 0 || tile.x >= gridResolution.x || tile.y < 0 || tile.y >= gridResolution.y;
            tileIndex = invalidIndex ? -1 : tile;

        }
        
        [BurstCompile]
        static int CalculateFlatTileIndex(ref float2 worldPos, ref float2 worldCorner, ref uint2 gridResolution, float cellSizeInv )
        {
            CalculateTileIndex(ref worldPos, ref worldCorner, ref gridResolution, cellSizeInv, out var tileIndex);
            if (tileIndex.x <= -1)
            {
                return -1;
            }

            return tileIndex.y * (int)gridResolution.x + tileIndex.x;
        }

        [BurstCompile]
        static int CalculateDataEntryIndex(int pageIndex, int linearDataIndex)
        {
            return pageIndex * INSTANCE_DATA_PAGE_SIZE + (linearDataIndex % INSTANCE_DATA_PAGE_SIZE);
        }
        
        

#if UNITY_EDITOR
        [UnityEditor.InitializeOnLoadMethod]
#else
        [UnityEngine.RuntimeInitializeOnLoadMethod(UnityEngine.RuntimeInitializeLoadType.AfterAssembliesLoaded)]
#endif
        private static void RegisterPrepareScatterPrefab()
        {
            ScatterPointCloudSystem.OnPostProcessScatterPrefab += OnPrepareScatterPrefab;
        }


        public static void OnPrepareScatterPrefab(NativeArray<Entity> prefabEntityGroup, EntityManager entityManager, int extraDataMask)
        {
            //for now we do naive check if the entity has MeshLODComponent, and if it doesn't dont add the tiledata (Should do a proper check later on)
            foreach (var e in prefabEntityGroup)
            {
                if (entityManager.HasComponent<Parent>(e))
                {
                    var parent = entityManager.GetComponentData<Parent>(e).Value;
                    if (entityManager.HasComponent<ScatteredInstanceNeedsPartitioningTag>(parent))
                    {
                        entityManager.AddComponent<ScatteredInstanceRenderTileData>(e); 
                    }
                }
                else if (entityManager.HasComponent<ScatteredInstanceParent>(e))
                {
                    var parent = entityManager.GetComponentData<ScatteredInstanceParent>(e).Value;
                    if (entityManager.HasComponent<ScatteredInstanceNeedsPartitioningTag>(parent))
                    {
                        entityManager.AddComponent<ScatteredInstanceRenderTileData>(e); 
                    }
                }
                else if(entityManager.HasComponent<ScatteredInstanceNeedsPartitioningTag>(e))
                {
                    entityManager.AddComponent<ScatteredInstanceRenderTileData>(e);
                }
            }
        }
    }
}
