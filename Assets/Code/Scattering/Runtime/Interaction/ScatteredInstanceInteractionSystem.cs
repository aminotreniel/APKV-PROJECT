using System.Collections.Generic;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using Unity.Transforms;
using UnityEngine;
using UnityEngine.Rendering;

namespace TimeGhost
{
    

    internal struct ScatteredInstanceSpatialInteractionSettingsData : IComponentData
    {
        public float2 CenterWS;
        public float ActiveRadius;
        public float ColliderSmoothingMargin;
        public bool PrioritizeGameCamera;
    }

    internal struct ScatteredInstanceColliderData : IComponentData
    {
        public float3 P0;
        public float3 P1;
        public float Radius;
    }
    
    [UpdateInGroup(typeof(PresentationSystemGroup))]
    [WorldSystemFilter(WorldSystemFilterFlags.Default)]
    public partial class ScatteredInstanceInteractionSystem : SystemBase
    {
        struct UploadEntry
        {
            public int activeTileIndex;
            public int absoluteTileIndex;
            public int pageOffset;
            public int entryCount;
            public uint uploadVersioning;
            public bool isLastUploadBatchForTile;
        }


        private int2 m_CurrentCenter;
        private float m_CurrentActiveRadius;
        private float m_TileSize;
        private int2 m_TilesPerDimension;

        private ScatteredInstanceInteractionGPU m_interactionGPU;

        private EntityQuery m_CollidersQuery;
        
        //tile refresh setup
        private NativeArray<int> m_PagesPerActiveTile;
        private NativeArray<int> m_TilesModified;
        private NativeArray<int> m_LastSeenTileRevision;
        
        private UnsafeAtomicCounter32 m_TotalActivePagesCount;
        private NativeList<int> m_TilesToRefresh;
        private NativeArray<int> m_TilesToFreeParams;
        private NativeArray<int3> m_TilesToReserveParams;

        //upload setup
        private NativeArray<ScatteredInstancePropertiesPacked> m_ScatteredInstanceUploadData;
        private NativeArray<ScatteredInstanceDataUploadBatch> m_ScatteredInstanceUploadBatchInfo;
        private NativeList<UploadEntry> m_UploadBatchEntries;
        private NativeList<int2> m_UploadCompletedForTiles;
        private NativeQueue<UploadEntry> m_UploadQueue;
        private NativeArray<uint> m_TileUploadVersion;
        
        //collider setup
        private UnsafeAtomicCounter32 m_ColliderCounter; //used for active colliders gather and active tiles per collider batch
        private NativeArray<ScatteredInstanceInteractionGPU.CapsuleColliderEntry> m_ActiveColliders;
        private NativeArray<uint> m_OffsetToActiveTilePages;
        private NativeArray<int> m_ColliderAffectedTiles;
        private NativeArray<int> m_ColliderMaskPerTile;
        private NativeReference<UnsafeList<uint3>> m_ColliderIntersectingTilesPagesAndMasks;
        private NativeArray<uint3> m_GatheredTilesPagesAndMasksArray; // colliders are processed in batches. However, we gather and store all batches here (batch size being min(c_MaxCollidersPerPatch, collidersToProcess)) so we can upload them all at once and then do the actual collider step later
        private NativeList<int> m_GatheredTilesPagesAndMasksCounts;

        private int2 m_PreviousActiveTileDimensions;
        private AABB m_PreviousCanvasArea;
        private float m_PreviousTileSize;
        
        private const int c_MaxCollidersPerPatch = 32;

        private bool m_NeedReinitialization = true;
        
        private double m_lastUpdateTime;
        private float m_timeStepDelta;
        private int2 m_minMaxStepsPerFrame;
        private int m_currentNumberOfUpdateSteps;
        
        
        protected override void OnCreate()
        {
            
            EntityManager.AddComponent<ScatteredInstanceSpatialInteractionSettingsData>(SystemHandle);
            ScatteredInstanceSpatialInteractionSettingsData settings = EntityManager.GetComponentData<ScatteredInstanceSpatialInteractionSettingsData>(SystemHandle);
            //set some defaults
            settings.ActiveRadius = 10;
            settings.CenterWS = float2.zero;
            settings.ColliderSmoothingMargin = 0.01f;

            m_CurrentCenter = int2.zero;
            m_CurrentActiveRadius = 0.0f;
            m_TileSize = 1.0f;
            m_TilesPerDimension = 1;

            m_interactionGPU = new ScatteredInstanceInteractionGPU();

            unsafe
            {
                m_TotalActivePagesCount =
                    new UnsafeAtomicCounter32(UnsafeUtility.Malloc(sizeof(int), 1, Allocator.Persistent));
                m_ColliderCounter = new UnsafeAtomicCounter32(UnsafeUtility.Malloc(sizeof(int), 1, Allocator.Persistent));

                *m_TotalActivePagesCount.Counter = 0;
                *m_ColliderCounter.Counter = 0;

            }
            m_TilesToRefresh = new NativeList<int>(100, Allocator.Persistent);
            m_UploadQueue = new NativeQueue<UploadEntry>(Allocator.Persistent);
            m_UploadBatchEntries = new NativeList<UploadEntry>(ScatteredInstanceInteractionGPU.GetMaximumNumberOfPagesToUploadPerBatch(), Allocator.Persistent);
            m_ScatteredInstanceUploadData = new NativeArray<ScatteredInstancePropertiesPacked>(
                ScatteredInstanceInteractionGPU.GetMaximumNumberOfPagesToUploadPerBatch() * ScatteredInstanceInteractionGPU.GetPageSize(), Allocator.Persistent);
            m_ScatteredInstanceUploadBatchInfo = new NativeArray<ScatteredInstanceDataUploadBatch>(
                ScatteredInstanceInteractionGPU.GetMaximumNumberOfPagesToUploadPerBatch(), Allocator.Persistent);
            m_UploadCompletedForTiles = new NativeList<int2>(10, Allocator.Persistent);

            m_CollidersQuery = EntityManager.CreateEntityQuery(ComponentType.ReadOnly<ScatteredInstanceColliderData>(),
                ComponentType.ReadOnly<LocalToWorld>());

            m_ColliderIntersectingTilesPagesAndMasks =
                new NativeReference<UnsafeList<uint3>>(Allocator.Persistent,
                    NativeArrayOptions.ClearMemory);
            
            m_GatheredTilesPagesAndMasksCounts = new NativeList<int>(64, Allocator.Persistent);

            RenderPipelineManager.beginContextRendering += OnBeginContextRendering;

            m_lastUpdateTime = SystemAPI.Time.ElapsedTime;
            m_timeStepDelta = SystemAPI.Time.fixedDeltaTime;
            m_minMaxStepsPerFrame = new int2(1, 6);
            
            m_NeedReinitialization = true;
        }
        
        
        
        protected override void OnDestroy()
        {
            RenderPipelineManager.beginContextRendering -= OnBeginContextRendering;
            m_interactionGPU.Deinit();
            FreeTileResource();
            m_CollidersQuery.Dispose();
            
        }

        void ResetTileDimensions(int2 tilesPerDimension)
        {
            if (m_PagesPerActiveTile.IsCreated)
            {
                m_PagesPerActiveTile.Dispose();
            }
            m_PagesPerActiveTile = new NativeArray<int>(tilesPerDimension.x * tilesPerDimension.y, Allocator.Persistent);

            if (m_TileUploadVersion.IsCreated)
            {
                m_TileUploadVersion.Dispose();
            }

            if (m_ColliderMaskPerTile.IsCreated)
            {
                m_ColliderMaskPerTile.Dispose();
            }

            if (m_ColliderAffectedTiles.IsCreated)
            {
                m_ColliderAffectedTiles.Dispose();
            }

            m_ColliderAffectedTiles = new NativeArray<int>(tilesPerDimension.x * tilesPerDimension.y, Allocator.Persistent);
            m_ColliderMaskPerTile = new NativeArray<int>(tilesPerDimension.x * tilesPerDimension.y, Allocator.Persistent);
            m_OffsetToActiveTilePages =
                new NativeArray<uint>(tilesPerDimension.x * tilesPerDimension.y + 1, Allocator.Persistent);

            m_TileUploadVersion = new NativeArray<uint>(tilesPerDimension.x * tilesPerDimension.y, Allocator.Persistent);

            m_TilesModified = new NativeArray<int>(tilesPerDimension.x * tilesPerDimension.y, Allocator.Persistent);
            m_LastSeenTileRevision = new NativeArray<int>(tilesPerDimension.x * tilesPerDimension.y, Allocator.Persistent);
            unsafe
            {
                UnsafeUtility.MemSet(m_PagesPerActiveTile.GetUnsafePtr(), 0, sizeof(int) * m_PagesPerActiveTile.Length);
                UnsafeUtility.MemSet(m_TileUploadVersion.GetUnsafePtr(), 0, sizeof(uint) * m_TileUploadVersion.Length);
                
            }
        }

        void FreeTileResource()
        {
            m_PagesPerActiveTile.Dispose();
            m_TileUploadVersion.Dispose();
            
            m_TilesToFreeParams.Dispose();
            m_TilesToReserveParams.Dispose();
            m_TilesToRefresh.Dispose();
            m_UploadQueue.Dispose();
            m_UploadBatchEntries.Dispose();
            m_ScatteredInstanceUploadData.Dispose();
            m_ScatteredInstanceUploadBatchInfo.Dispose();
            m_UploadCompletedForTiles.Dispose();

            if (m_TilesModified.IsCreated)
            {
                m_TilesModified.Dispose();
            }

            if (m_LastSeenTileRevision.IsCreated)
            {
                m_LastSeenTileRevision.Dispose();

            }
            
            m_GatheredTilesPagesAndMasksCounts.Dispose();
            
            if (m_ActiveColliders.IsCreated)
            {
                m_ActiveColliders.Dispose();
            }
            
            if (m_ColliderIntersectingTilesPagesAndMasks.Value.IsCreated)
            {
                m_ColliderIntersectingTilesPagesAndMasks.Value.Dispose();
            }

            if (m_OffsetToActiveTilePages.IsCreated)
            {
                m_OffsetToActiveTilePages.Dispose();
            }

            if (m_ColliderAffectedTiles.IsCreated)
            {
                m_ColliderAffectedTiles.Dispose();
            }

            m_ColliderIntersectingTilesPagesAndMasks.Dispose();

            if (m_ColliderMaskPerTile.IsCreated)
            {
                m_ColliderMaskPerTile.Dispose();
            }

            if (m_GatheredTilesPagesAndMasksArray.IsCreated)
            {
                m_GatheredTilesPagesAndMasksArray.Dispose();
            }

            unsafe
            {
                UnsafeUtility.Free(m_TotalActivePagesCount.Counter, Allocator.Persistent);
                UnsafeUtility.Free(m_ColliderCounter.Counter, Allocator.Persistent);
            }
        }

        void OnBeginContextRendering(ScriptableRenderContext context, List<Camera> cameras)
        {
            
            ScatteredInstanceSpatialInteractionSettingsData settings = EntityManager.GetComponentData<ScatteredInstanceSpatialInteractionSettingsData>(SystemHandle);
            bool tryToPickSceneCamera = !settings.PrioritizeGameCamera;
            
            Camera cameratoFollow = null;
            //for now just pick scene camera if available. Otherwise game view (for testing)
            foreach (var cam in cameras)
            {
                if (cam.cameraType == CameraType.Game)
                {
                    if (cameratoFollow == null || !tryToPickSceneCamera)
                    {
                        cameratoFollow = cam;
                    }
                }

                if (cam.cameraType == CameraType.SceneView)
                {
                    if (cameratoFollow == null || tryToPickSceneCamera)
                    {
                        cameratoFollow = cam;
                    }
                }
            }

            if (cameratoFollow != null)
            {
                
                float3 camPos = cameratoFollow.transform.position;
                settings.CenterWS = camPos.xz;
                EntityManager.SetComponentData(SystemHandle, settings);
            }
            
        }

        protected override void OnUpdate()
        {
            
            SystemHandle partitionSystem = World.GetExistingSystem<ScatteredInstanceSpatialPartitioningSystem>();
            if (partitionSystem == SystemHandle.Null) return;

            ScatteredInstanceSpatialPartitioningData partitioningData = EntityManager.GetComponentData<ScatteredInstanceSpatialPartitioningData>(partitionSystem);
            ScatteredInstanceSpatialInteractionSettingsData settings = EntityManager.GetComponentData<ScatteredInstanceSpatialInteractionSettingsData>(SystemHandle);

            if ((ScatteredInstanceSpatialPartitioningSystem.INSTANCE_DATA_PAGE_SIZE % ScatteredInstanceInteractionGPU.GetPageSize()) != 0)
            {
                Debug.LogError("ScatteredInstanceInteractionGPU page size is not multiple of ScatteredInstanceSpatialPartitioningSystem.INSTANCE_DATA_PAGE_SIZE, this is not supported!");
                return;
            }

            //update timestep
            {
                var currentTime = SystemAPI.Time.ElapsedTime;
                var deltaTime = currentTime - m_lastUpdateTime;
                m_currentNumberOfUpdateSteps = math.clamp((int)math.floor(deltaTime / m_timeStepDelta), m_minMaxStepsPerFrame.x,
                    m_minMaxStepsPerFrame.y);
            }
            
            int2 gridResolution = (int2)partitioningData.CalculateGridResolution();
            if (gridResolution.x == 0 || gridResolution.y == 0)
            {
                m_PreviousCanvasArea.Center = 0;
                m_PreviousCanvasArea.Extents = 0;
                return;
            }

            bool partitioningHasChanged = false;
            if (math.any(m_PreviousCanvasArea.Center != partitioningData.CanvasArea.Center) || math.any(m_PreviousCanvasArea.Extents != partitioningData.CanvasArea.Extents) || m_PreviousTileSize != partitioningData.CellSizeInMeters)
            {
                partitioningHasChanged = true;
                m_PreviousCanvasArea = partitioningData.CanvasArea;
                m_PreviousTileSize = partitioningData.CellSizeInMeters;
            }
            
            int2 tileNumberNow = 2 * (int)math.ceil(settings.ActiveRadius / partitioningData.CellSizeInMeters);
            tileNumberNow = math.max(tileNumberNow, 1);
            //the active area can be at maximum the same size as the smaller dimension of the whole area
            tileNumberNow = math.min(tileNumberNow, gridResolution);
            
            bool reinitializationNeeded = math.any(m_PreviousActiveTileDimensions != tileNumberNow) || partitioningHasChanged;

            if (m_NeedReinitialization)
            {
                reinitializationNeeded = true;
                m_NeedReinitialization = false;
            }
            
            m_PreviousActiveTileDimensions = tileNumberNow;
            
            bool resetAllTiles = false;
            if (reinitializationNeeded)
            {
                m_CurrentActiveRadius = settings.ActiveRadius;
                m_TileSize = partitioningData.CellSizeInMeters;
                m_PreviousActiveTileDimensions = m_TilesPerDimension;
                m_TilesPerDimension = tileNumberNow;
                resetAllTiles = true;
                ResetTileDimensions(m_TilesPerDimension);
                m_interactionGPU.Init(m_TilesPerDimension, gridResolution);
                
            }

            m_interactionGPU.SetColliderSmoothingMargin(settings.ColliderSmoothingMargin);
            m_interactionGPU.PushConstantsToGPU();
            
            int2 clampedNewCenter;
            int2 tilesPerDirection = m_TilesPerDimension / 2;
            int2 oddTiles = m_TilesPerDimension % 2;
            float2 worldCorner = new float2(partitioningData.CanvasArea.Min.xz);
            clampedNewCenter = (int2)math.floor(((settings.CenterWS - worldCorner)  / m_TileSize) + 0.5f);
            clampedNewCenter.x = math.clamp(clampedNewCenter.x, tilesPerDirection.x, gridResolution.x - tilesPerDirection.x - oddTiles.x);
            clampedNewCenter.y = math.clamp(clampedNewCenter.y, tilesPerDirection.y, gridResolution.y - tilesPerDirection.y - oddTiles.y);

            if (math.abs(clampedNewCenter.x - m_CurrentCenter.x) >= m_TilesPerDimension.x ||
                math.abs(clampedNewCenter.y - m_CurrentCenter.y) >= m_TilesPerDimension.y)
            {
                resetAllTiles = true;
            }
            var dependency = Dependency;
            dependency = CheckTilesModified(ref partitioningData, dependency);
            dependency.Complete();
            
            if (!resetAllTiles)
            {
                if (clampedNewCenter.x != m_CurrentCenter.x || clampedNewCenter.y != m_CurrentCenter.y)
                {
                    CalculateTilesToRefresh(m_CurrentCenter, clampedNewCenter, m_TilesPerDimension, m_TilesModified,  ref m_TilesToRefresh);
                }
                for (int i = 0; i < m_TilesModified.Length; ++i)
                {
                    if (m_TilesModified[i] != 0)
                    {
                        m_TilesToRefresh.Add(i);
                    }
                }
            }
            else
            {
                //TODO: optimize the full reset. This doesn't happen runtime tho, so not a high prio
                if (m_TilesToRefresh.Capacity < m_TilesPerDimension.x * m_TilesPerDimension.y)
                {
                    m_TilesToRefresh.SetCapacity(m_TilesPerDimension.x * m_TilesPerDimension.y);
                }
                
                for (int i = 0; i < m_TilesPerDimension.x * m_TilesPerDimension.y; ++i)
                {
                    m_TilesToRefresh.Add(i);
                }
                ClearUploadQueue();
            }
            
            m_CurrentCenter = clampedNewCenter;

            
            bool pagesRefreshed = false;
            bool uploadPrepared = false;
            
            if (!m_TilesToRefresh.IsEmpty)
            {
                dependency = CalculateTileRefreshParameters(ref partitioningData, m_TilesToRefresh, (uint2)m_CurrentCenter, dependency);
                pagesRefreshed = true;
            }
            else
            {
                dependency = PrepareNextUploadBatch(ref partitioningData, dependency);
                uploadPrepared = true;
            }
            
            
            
            //complete here to make sure data is up to date for GPU (maybe start these jobs earlier to make the sync point less immediate)
            dependency.Complete();
            
            if (pagesRefreshed)
            {
                int pagesCount;
                unsafe
                {
                    pagesCount = *m_TotalActivePagesCount.Counter;
                }
                m_interactionGPU.RefreshPages(m_TilesToReserveParams, m_TilesToFreeParams, m_TilesToRefresh.Length, pagesCount);
            }
            if (uploadPrepared)
            {
                UploadPreparedBatch();
            }

            //actual simulation stuff
            
            //prepare simulation 
            int colliderCount;
            int masksTilesAndPagesCount;
            
            bool collidersPresent = GatherColliderBatchData(ref partitioningData, dependency, out colliderCount, out masksTilesAndPagesCount);
            if (collidersPresent)
            {
                UploadColliderBatchData(colliderCount, masksTilesAndPagesCount);
            }
            m_interactionGPU.PrepareSimulation();
            
            //step simulation
            for (int step = 0; step < m_currentNumberOfUpdateSteps; ++step)
            {
                if (collidersPresent)
                {
                    int colliderOffset = 0;
                    int tilePagesMasksOffset = 0;
                    for (int i = 0; i < m_GatheredTilesPagesAndMasksCounts.Length; ++i)
                    {
                        m_interactionGPU.StepCollisions(colliderOffset, tilePagesMasksOffset, m_GatheredTilesPagesAndMasksCounts[i], m_timeStepDelta);
                        colliderOffset += c_MaxCollidersPerPatch;
                        tilePagesMasksOffset += m_GatheredTilesPagesAndMasksCounts[i];
                    }
                }
                m_interactionGPU.StepSimulation(m_timeStepDelta);
            }
            
            m_TilesToRefresh.Clear();

            Dependency = dependency;
        }

        JobHandle CheckTilesModified(ref ScatteredInstanceSpatialPartitioningData partitioningData, JobHandle deps)
        {
            deps = new CheckTilesModifiedJob
            {
                AbsoluteGridResolution = partitioningData.CalculateGridResolution(),
                ActiveAreaCenter = (uint2)m_CurrentCenter,
                TilesPerActiveDimension = (uint2)m_TilesPerDimension,
                TileRevisions = partitioningData.TileRevisionNumber,
                LastSeenTileRevisions = m_LastSeenTileRevision,
                PageChangesFoundPerTile = m_TilesModified

            }.Schedule(m_TilesPerDimension.x * m_TilesPerDimension.y, 64, deps);

            return deps;
        }
        
        JobHandle CalculateTileRefreshParameters(ref ScatteredInstanceSpatialPartitioningData partitioningData, NativeList<int> tilesToRefresh, uint2 newCenter, JobHandle deps)
        {
            if (!m_TilesToFreeParams.IsCreated || m_TilesToFreeParams.Length < tilesToRefresh.Length)
            {
                if (m_TilesToFreeParams.IsCreated)
                {
                    m_TilesToFreeParams.Dispose();
                    m_TilesToReserveParams.Dispose();
                }
                
                m_TilesToFreeParams = new NativeArray<int>(tilesToRefresh.Length, Allocator.Persistent);
                m_TilesToReserveParams = new NativeArray<int3>(tilesToRefresh.Length, Allocator.Persistent);
            }
            

            var jobHandle = new CalculateTileRefreshParamsJob
            {
                AbsoluteGridResolution = partitioningData.CalculateGridResolution(),
                ActiveAreaCenter = newCenter,
                ActiveTilesToRefresh = tilesToRefresh,
                FreeTilesParams = m_TilesToFreeParams,
                InstancesPerAbsoluteTile = partitioningData.InstancesPerTile,
                PageCountPerActiveTile = m_PagesPerActiveTile,
                ReserveTilesParams = m_TilesToReserveParams,
                TilesPerActiveDimension = (uint2)m_TilesPerDimension,
                TotalPageCount = m_TotalActivePagesCount,
                UploadQueue = m_UploadQueue.AsParallelWriter(),
                PerTileUploadVersion = m_TileUploadVersion
            }.Schedule(tilesToRefresh.Length,  8, deps);

            return jobHandle;
        }

        JobHandle PrepareNextUploadBatch(ref ScatteredInstanceSpatialPartitioningData partitioningData, JobHandle deps)
        {
            m_UploadBatchEntries.Clear();
            m_UploadCompletedForTiles.Clear();
            
            if (m_UploadQueue.IsEmpty()) return deps;
            
            int numberOfUploadsIssued = 0;
            
            while (m_UploadQueue.TryDequeue(out var entry))
            {
                //if the upload entry has different versioning, it means that it's "old" and the tile is already been reused
                if (entry.uploadVersioning == m_TileUploadVersion[entry.activeTileIndex])
                {
                    if (entry.isLastUploadBatchForTile)
                    {
                        m_UploadCompletedForTiles.Add(new int2(entry.activeTileIndex, entry.absoluteTileIndex));
                    }

                    if (entry.entryCount > 0)
                    {
                        m_UploadBatchEntries.Add(entry);
                        ++numberOfUploadsIssued;
                        if (numberOfUploadsIssued ==
                            ScatteredInstanceInteractionGPU.GetMaximumNumberOfPagesToUploadPerBatch())
                        {
                            break;
                        }
                    }
                    
                }
                
            }
  
            var jobHandle = new FillNextUploadBatchJob
            {
                InstanceDataPages = partitioningData.InstanceDataPages.Value,
                PerTileReservedPages = partitioningData.PerTileReservedPages,
                UploadBatchEntries = m_UploadBatchEntries,
                UploadDataBuffer = m_ScatteredInstanceUploadData,
                UploadDataBatch = m_ScatteredInstanceUploadBatchInfo
            }.Schedule(m_UploadBatchEntries.Length, 1, deps);

            return jobHandle;
        }
        
        void UploadPreparedBatch()
        {

            if (!m_UploadCompletedForTiles.IsEmpty)
            {
                var uploadCompletedArray = m_UploadCompletedForTiles.ToArray(Allocator.Temp);
                m_interactionGPU.SetupActiveTileToAbsoluteTileMapping(uploadCompletedArray);
                uploadCompletedArray.Dispose();
            }

            if (m_UploadBatchEntries.Length > 0)
            {
                m_interactionGPU.UploadPages(m_ScatteredInstanceUploadBatchInfo, m_ScatteredInstanceUploadData, m_UploadBatchEntries.Length);
            }
        }
        
        void ClearUploadQueue()
        {
            m_UploadQueue.Clear();
        }

        bool GatherColliderBatchData(ref ScatteredInstanceSpatialPartitioningData partitioningData, JobHandle deps, out int colliderCount, out int gatheredTilesPagesAndMasksCount)
        {
            colliderCount = 0;
            gatheredTilesPagesAndMasksCount = 0;
            
            int maximumColliderCount = m_CollidersQuery.CalculateEntityCount();
            if(maximumColliderCount == 0) return false;
            
            if (!m_ActiveColliders.IsCreated || m_ActiveColliders.Length < maximumColliderCount)
            {
                if (m_ActiveColliders.IsCreated)
                    m_ActiveColliders.Dispose();
                m_ActiveColliders = new NativeArray<ScatteredInstanceInteractionGPU.CapsuleColliderEntry>(maximumColliderCount, Allocator.Persistent);
            }

            unsafe
            {
                *m_ColliderCounter.Counter = 0;
            }

            var jobHandle = new GatherRelevantCollidersJob
            {
                ActiveAreaCenter = (uint2) m_CurrentCenter,
                ActiveTilesPerDimension = (uint2) m_TilesPerDimension,
                CellSizeInMeters = partitioningData.CellSizeInMeters,
                WorldCorner = partitioningData.CanvasArea.Min.xz,
                
                ColliderDataType = GetComponentTypeHandle<ScatteredInstanceColliderData>(),
                TransformType = GetComponentTypeHandle<LocalToWorld>(),
                ColliderCount = m_ColliderCounter,
                CollidersArray = m_ActiveColliders,
                
            }.ScheduleParallel(m_CollidersQuery, deps);
            
            //need to know the collider count here: TODO: investigate if there is a way to not block here
            jobHandle.Complete();

            int activeCollidersToProcess; 
            int collidersToProcessOffset = 0;
            unsafe
            {
                activeCollidersToProcess = *m_ColliderCounter.Counter;
                *m_ColliderCounter.Counter = 0;
            }

            m_GatheredTilesPagesAndMasksCounts.Clear();
            
            int numberOfGatheredTilesPagesAndMasks = 0;
            while (activeCollidersToProcess > 0)
            {

                int collidersToProcessCurrentBatch = math.min(activeCollidersToProcess, (int)c_MaxCollidersPerPatch);
                
                //clear masks 
                jobHandle = new ClearColliderPerTileMasksJob
                {
                    Masks = m_ColliderMaskPerTile
                }.Schedule(m_ColliderMaskPerTile.Length, 64, jobHandle);
                

                //gather tiles affected by collider batch
                jobHandle = new GatherTilesAffectedByCollidersJob
                {
                    ActiveTileCount = m_ColliderCounter,
                    ActiveAreaCenter = (uint2) m_CurrentCenter,
                    ActiveTilesPerDimension = (uint2) m_TilesPerDimension,
                    CellSizeInMeters = partitioningData.CellSizeInMeters,
                    WorldCorner = partitioningData.CanvasArea.Min.xz,
                    ColliderAffectedTiles = m_ColliderAffectedTiles,
                    ColliderAffectedTilesPageCount = m_OffsetToActiveTilePages,
                    ColliderOffset = collidersToProcessOffset,
                    CollidersArray = m_ActiveColliders,
                    PagesPerTile = m_PagesPerActiveTile,
                    PerTileColliderMask = m_ColliderMaskPerTile
                }.Schedule(collidersToProcessCurrentBatch, 1, jobHandle);
                
                jobHandle = new ResizeActiveColliderTilesArray
                {
                    AffectedTilesCount = m_ColliderCounter,
                    ColliderAffectedTilesPageCount = m_OffsetToActiveTilePages,
                    ColliderIntersectingTilePageAndMask = m_ColliderIntersectingTilesPagesAndMasks
                }.Schedule(jobHandle);

                jobHandle.Complete(); //complete here for now, as we need to know the total number of tiles affected. Could potentially just launch for all tiles but need to investigate if there is a better way

                int affectedTilesCountForCurrentBatch;
                unsafe
                {
                    affectedTilesCountForCurrentBatch = *m_ColliderCounter.Counter;
                    *m_ColliderCounter.Counter = 0;
                }
                //generate list of [tile, pageIndex, mask] for the GPU
                jobHandle = new CalculateAffectedTilesPagesAndMaskJob
                {
                    
                    ColliderIntersectingTilePageAndMaskArray = m_ColliderIntersectingTilesPagesAndMasks,
                    PerTileOffsetToIntersectingTilePages = m_OffsetToActiveTilePages,
                    ColliderAffectedTiles = m_ColliderAffectedTiles,
                    PerTileColliderMask = m_ColliderMaskPerTile
                }.Schedule(affectedTilesCountForCurrentBatch, 4, jobHandle);

                jobHandle.Complete();
                if (affectedTilesCountForCurrentBatch > 0)
                {
                    int numberOfIntersectingTilesPagesAndMasksEntries = (int)m_OffsetToActiveTilePages[affectedTilesCountForCurrentBatch];
                    var tilePageMaskSrcArray = m_ColliderIntersectingTilesPagesAndMasks.Value;
                    
                    if (!m_GatheredTilesPagesAndMasksArray.IsCreated || m_GatheredTilesPagesAndMasksArray.Length < (numberOfGatheredTilesPagesAndMasks + numberOfIntersectingTilesPagesAndMasksEntries))
                    {
                        m_GatheredTilesPagesAndMasksArray.ResizeArray(numberOfGatheredTilesPagesAndMasks + numberOfIntersectingTilesPagesAndMasksEntries);
                    }
                    
                    //copy tiles, pages and masks into a buffer and process next batch
                    for (int i = 0; i < numberOfIntersectingTilesPagesAndMasksEntries; ++i)
                    {
                        int offset = numberOfGatheredTilesPagesAndMasks + i;
                        m_GatheredTilesPagesAndMasksArray[offset] = tilePageMaskSrcArray[i];
                    }
                    
                    m_GatheredTilesPagesAndMasksCounts.Add(numberOfIntersectingTilesPagesAndMasksEntries);
                    numberOfGatheredTilesPagesAndMasks += numberOfIntersectingTilesPagesAndMasksEntries;
                }
                

                activeCollidersToProcess -= collidersToProcessCurrentBatch;
                collidersToProcessOffset += collidersToProcessCurrentBatch;

            }
            
            colliderCount = collidersToProcessOffset;
            gatheredTilesPagesAndMasksCount = numberOfGatheredTilesPagesAndMasks;
            
            return true;
        }

        void UploadColliderBatchData(int colliderCount, int tilesPagesAndMasksCount)
        {
            m_interactionGPU.UploadColliderData(m_ActiveColliders, colliderCount, m_GatheredTilesPagesAndMasksArray, tilesPagesAndMasksCount);
        }
        
        
        static void CalculateTilesToRefresh(int2 oldCenter, int2 newCenter, int2 tilesPerDimension, NativeArray<int> tilesAlreadyToBeRefreshed, ref NativeList<int> tilesToRefresh)
        {

            int2 tilesPerDirection = tilesPerDimension / 2;
            int2 oddTiles = tilesPerDimension % 2;
            //TODO: could just iterate through horizontal, vertical and corners to gather refreshed tiles. For now do a naive loop through the whole thing
            for (int y = oldCenter.y - tilesPerDirection.y; y < oldCenter.y + tilesPerDirection.y + oddTiles.y; ++y)
            {
                for (int x = oldCenter.x  - tilesPerDirection.x; x < oldCenter.x + tilesPerDirection.x + oddTiles.x; ++x)
                {
                    bool tileNeedsRefresh = false;
                    if (math.abs(newCenter.x - x - 0.5f)  > tilesPerDirection.x)
                    {
                        tileNeedsRefresh = true;
                    } else if (math.abs(newCenter.y - y - 0.5f) > tilesPerDirection.y)
                    {
                        tileNeedsRefresh = true;
                    }

                    if (tileNeedsRefresh)
                    {
                        int flatActiveIndex = AbsoluteToActiveFlattenedTileIndex(x, y, tilesPerDimension);
                        if (flatActiveIndex < 0)
                        {
                            Debug.Log("Tiles going outside of valid areas!");
                            continue;
                        }

                        if (tilesAlreadyToBeRefreshed[flatActiveIndex] == 0)
                        {
                            tilesToRefresh.Add(flatActiveIndex);
                        }
                    }
                }
            }
        }

        private static int WrapTileIndex(int val, int tilesPerDimension)
        {
            var mod = val % tilesPerDimension;
            return mod;
        }

        private static int2 AbsoluteToActiveTileIndex(int x, int y, int2 tilesPerDimension)
        {
            int wx = WrapTileIndex(x, tilesPerDimension.x);
            int wy = WrapTileIndex(y, tilesPerDimension.y);

            return new int2(wx, wy);
        }

        private static int AbsoluteToActiveFlattenedTileIndex(int x, int y, int2 tilesPerDimension)
        {
            int2 tileIndex = AbsoluteToActiveTileIndex(x, y, tilesPerDimension);
            return tileIndex.x + tileIndex.y * tilesPerDimension.x;
        }

        private static int2 ActiveFlattenedTileIndexToAbsoluteTileIndex(uint wrappedIndex, uint2 tilesPerDimension, ref uint2 center)
        {
            uint wx = wrappedIndex % tilesPerDimension.x;
            uint wy = wrappedIndex / tilesPerDimension.x;
            uint2 tilesPerDirection = tilesPerDimension / 2;

            int2 tilesPerDimensionInt = (int2) tilesPerDimension;
            
            int2 corner = (int2)center;
            corner.x -= (int) tilesPerDirection.x;
            corner.y -= (int) tilesPerDirection.y;

            int tileMultiplesX = (corner.x / tilesPerDimensionInt.x) + ((corner.x % tilesPerDimensionInt.x) > wx ? 1 : 0);
            int tileMultiplesY = (corner.y / tilesPerDimensionInt.y) + ((corner.y % tilesPerDimensionInt.y) > wy ? 1 : 0);

            int2 absoluteIndex;
            absoluteIndex.x = tileMultiplesX * tilesPerDimensionInt.x + (int)wx;
            absoluteIndex.y = tileMultiplesY * tilesPerDimensionInt.y + (int)wy;
            return absoluteIndex;
        }

        private static int ActiveFlattenedTileIndexToAbsoluteFlattenedTileIndex(uint wrappedIndex, uint2 tilesPerDimension, ref uint2 center, ref uint2 gridResolution)
        {
            int2 absoluteIndex = ActiveFlattenedTileIndexToAbsoluteTileIndex(wrappedIndex, tilesPerDimension, ref center);
            if (absoluteIndex.x < 0 || absoluteIndex.y < 0)
                return -1;
            return absoluteIndex.x + absoluteIndex.y * (int)gridResolution.x;

        }

        private static bool CheckTileCapsuleCollision(ref float2 p0, ref float2 p1, float radius, ref float2 tileCenter,
            float tileWidth)
        {
            float halfTileWidth = tileWidth / 2;
            //calculate closest point to line segment defining the capsule
            float2 closestPoint;
            {
                float2 v = p1 - p0;
                float vLen = math.length(v);
                if (vLen > 0.0f)
                {
                    v /= vLen;

                    float2 toCenter = tileCenter - p0;
                    float dot = math.dot(toCenter, v);

                    if (dot < 0)
                    {
                        closestPoint = p0;
                    } 
                    else if (dot > vLen)
                    {
                        closestPoint = p1;
                    }
                    else
                    {
                        closestPoint = p0 + dot * v;
                    }
                }
                else
                {
                    closestPoint = p0;
                }
                
            }
            
            //check intersection
            {
                float2 closestPointToCenter = tileCenter - closestPoint;
                float distSqrToClosestPoint = math.lengthsq(closestPointToCenter);

                float radiusWidthSqr = halfTileWidth * halfTileWidth + halfTileWidth * halfTileWidth + radius * radius;

                if (distSqrToClosestPoint > radiusWidthSqr)
                {
                    return false;
                }

                
                float vLen = math.length(closestPointToCenter);
                if (vLen > 0)
                {
                    closestPointToCenter /= vLen;
                }
                

                //move closest point from the capsules center to its hull (but not further than tile center). 
                closestPoint += closestPointToCenter * math.min(vLen, radius);

                if (closestPoint.x >= tileCenter.x - halfTileWidth && closestPoint.x <= tileCenter.x + halfTileWidth &&
                    closestPoint.y >= tileCenter.y - halfTileWidth && closestPoint.y <= tileCenter.y + halfTileWidth)
                {
                    return true;
                }
            }
            
            return false;
        }

        private static void CalculateMaxActiveRegionOverlapWithCapsule(float2 wp0, float2 wp1, float radius,
            float2 worldCorner, uint2 activeAreaCenter, float cellSizeInv, uint2 tilesPerDimension, out int2 colliderTileMinGlobal, out int2 colliderTileMaxGlobal)
        {

                uint2 tilesPerDirection = tilesPerDimension / 2;
                uint2 oddTiles = tilesPerDimension % 2;
                int2 minTileIndex = (int2)activeAreaCenter - new int2(tilesPerDirection);
                int2 maxTileIndex = (int2)activeAreaCenter + new int2(tilesPerDirection + oddTiles - 1);

                float2 minCorner = math.min(wp0 - radius, wp1 - radius);
                float2 maxCorner = math.max(wp0 + radius, wp1 + radius);

                minCorner -= worldCorner;
                maxCorner -= worldCorner;

                colliderTileMinGlobal = new int2(Mathf.FloorToInt(minCorner.x * cellSizeInv), Mathf.FloorToInt(minCorner.y * cellSizeInv));
                colliderTileMaxGlobal = new int2(Mathf.FloorToInt(maxCorner.x * cellSizeInv), Mathf.FloorToInt(maxCorner.y * cellSizeInv));

                colliderTileMinGlobal = math.clamp(colliderTileMinGlobal, minTileIndex, maxTileIndex);
                colliderTileMaxGlobal = math.clamp(colliderTileMaxGlobal, minTileIndex, maxTileIndex);

        }
        
    }
    
}
