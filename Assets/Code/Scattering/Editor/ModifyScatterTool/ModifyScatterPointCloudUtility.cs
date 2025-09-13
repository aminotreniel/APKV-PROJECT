using System;
using System.Collections.Generic;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace TimeGhost
{
    public class ModifyScatterPointCloudUtility 
    {
        private struct Instance
        {
            public GameObject go;
            public ScatterDataOverride scatterData;
        }

        private class OverrideDataLists
        {
            public List<PointCloudFromHoudiniAsset.OverridePointCloudEntry> entries;
            public List<float4> originalPosRadius;

        }

        private Dictionary<int, Tuple<PointCloudFromHoudiniAsset, int>> m_PointCloudDataIndexToAssetAndIndexMapping;

        private PointCloudUtility.PointCloudDataEntry[] m_PointCloudEntries;
        private PointCloudUtility.PointCloudPartitionOutput m_PartitionOutput;
        private NativeArray<int> m_PointsToLoadIndices;
        private NativeArray<int> m_PointsToUnloadIndices;
        private NativeArray<int> m_PerPointLoadStatus;
        
        private UnsafeAtomicCounter32 m_PointsToLoadCount;
        private UnsafeAtomicCounter32 m_PointsToUnloadCount;

        private bool[] m_HasChangedDataEntry;
        private ScatterDataOverride.PointCloudOverrideDataEntry[] m_DataEntry;
        private Instance[] m_Instances;
        
        private GameObject m_Root;
        private GameObject[] m_PerPointCloudRoot;
        
        private List<int> m_LastCellIndicesInside;

        private float m_Radius = 10.0f;
        private float m_OmissionArea = 0.00001f;
        
        public void BeginEdit(ScatterPointCloudAuthoring[] authoringComponents, float partitionSize)
        {
            ReleaseResources();
            m_PointCloudDataIndexToAssetAndIndexMapping = new Dictionary<int, Tuple<PointCloudFromHoudiniAsset, int>>(64);
            List<PointCloudUtility.PointCloudDataEntry> dataList = new List<PointCloudUtility.PointCloudDataEntry>();
            int overallPointCount = 0;
            {
                foreach (var source in authoringComponents)
                {
                    var pcDataArray = source.pointCloudAsset.GetPointCloudData();
                    for (int i = 0; i < pcDataArray.Length; ++i)
                    {
                        var pc = pcDataArray[i];
                        if (pc.positions == null || pc.positions.Length == 0 || pc.prefab == null)
                            continue;

                        m_PointCloudDataIndexToAssetAndIndexMapping[dataList.Count] = new Tuple<PointCloudFromHoudiniAsset, int>(source.pointCloudAsset, i);
                        
                        dataList.Add(new PointCloudUtility.PointCloudDataEntry{transform = source.transform.localToWorldMatrix, pcData = pc});
                    }
                }
            }

            m_PointCloudEntries = dataList.ToArray();
            m_PartitionOutput = PointCloudUtility.PartitionPointCloudDataToTiles(m_PointCloudEntries, partitionSize, Allocator.Persistent);

            int pointCount = m_PartitionOutput.toPointCloudDataEntryMapping.Length;

            m_PointsToLoadIndices = new NativeArray<int>(pointCount, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            m_PointsToUnloadIndices = new NativeArray<int>(pointCount, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            m_PerPointLoadStatus = new NativeArray<int>(pointCount, Allocator.Persistent, NativeArrayOptions.ClearMemory);

            m_Instances = new Instance[pointCount];
            m_DataEntry = new ScatterDataOverride.PointCloudOverrideDataEntry[pointCount];
            m_HasChangedDataEntry = new bool[pointCount];
            
            Array.Fill(m_HasChangedDataEntry, false);
            
            unsafe
            {
                m_PointsToLoadCount = new UnsafeAtomicCounter32(UnsafeUtility.Malloc(sizeof(int), 1, Allocator.Persistent));
                m_PointsToUnloadCount = new UnsafeAtomicCounter32(UnsafeUtility.Malloc(sizeof(int), 1, Allocator.Persistent));
            }

            m_LastCellIndicesInside = new List<int>(64);

            m_Root = new GameObject("ModPointCloudRoot");
            m_Root.hideFlags = HideFlags.DontSave | HideFlags.NotEditable;

            m_PerPointCloudRoot = new GameObject[m_PointCloudEntries.Length];

            for (int i = 0; i < m_PerPointCloudRoot.Length; ++i)
            {
                var go = new GameObject($"Root{i}");
                go.transform.SetParent(m_Root.transform ,false);
                var pcEntry = m_PointCloudEntries[i];
                Matrix4x4 transf = pcEntry.transform;
                Vector3 gPos = new Vector3( transf.m03, transf.m13, transf.m23 );
                Quaternion gRotation = transf.rotation;
                Vector3 gScale = transf.lossyScale;

                go.transform.localPosition = gPos;
                go.transform.localRotation = gRotation;
                go.transform.localScale = gScale;
                m_PerPointCloudRoot[i] = go;
            }
            
            RegisterForUpdates(true);
            
            m_PartitionOutput.pendingJobs.Complete();
            
        }

        
        
        public void SetVisibleRadius(float radius)
        {
            m_Radius = radius;
        }

        public void SetOmissionRadius(float r)
        {
            m_OmissionArea = r;
        }
        
        public void Tick()
        {
            var cameras = SceneView.GetAllSceneCameras();
            if (cameras != null && cameras.Length > 0)
            {
                Camera cam = cameras[0];
                if (cam != null)
                {
                    UpdateVisibleObjects(cam.transform.position, m_Radius);
                }
            }
        }

        private void UpdateVisibleObjects(float3 center, float radius)
        {
            
            JobHandle deps = default;
            deps = CalculatePointsToLoadAndUnload(center, radius, deps);
            deps.Complete();

            int loadCount;
            int unloadCount;

            unsafe
            {
                loadCount = *m_PointsToLoadCount.Counter;
                unloadCount = *m_PointsToUnloadCount.Counter;
            }

            for (int i = 0; i < unloadCount; ++i)
            {
                int unloadIndex = m_PointsToUnloadIndices[i];
                var instance = m_Instances[unloadIndex];
                RecordPotentialChanges(unloadIndex);
                CoreUtils.Destroy(instance.go);
                instance.go = null;
                m_Instances[unloadIndex] = instance;

            }
            
            for (int i = 0; i < loadCount; ++i)
            {
                int loadIndex = m_PointsToLoadIndices[i];
                int2 indices = m_PartitionOutput.toPointCloudDataEntryMapping[loadIndex];
                var pointCloudEntry = m_PointCloudEntries[indices.x];

                var instance = Instantiate(pointCloudEntry, indices.y);
                instance.go.transform.SetParent(m_PerPointCloudRoot[indices.x].transform, false);
                m_Instances[loadIndex] = instance;
            }
            
        }
        
        
        public void EndEdit(bool saveChanges)
        {
            if (saveChanges)
            {
                GatherPotentialChangesFromInstances();
                WriteChangedEntries();
            }
            
            ReleaseResources();
            CoreUtils.Destroy(m_Root);
            RegisterForUpdates(false);
        }

        private void WriteChangedEntries()
        {
            Dictionary<int, OverrideDataLists> overridePerPointCloudData = new Dictionary<int, OverrideDataLists>(64);
            HashSet<PointCloudFromHoudiniAsset> assetsModified = new HashSet<PointCloudFromHoudiniAsset>(64);

            int numberOfChanges = 0;
            for (int i = 0; i < m_Instances.Length; ++i)
            {
                if (m_HasChangedDataEntry[i])
                {
                    int2 pointCloudMapping = m_PartitionOutput.toPointCloudDataEntryMapping[i];
                    if (!overridePerPointCloudData.TryGetValue(pointCloudMapping.x, out var overrideData))
                    {
                        overrideData = new OverrideDataLists()
                        {
                            originalPosRadius = new List<float4>(64),
                            entries = new List<PointCloudFromHoudiniAsset.OverridePointCloudEntry>(64)
                        };
                        
                        overridePerPointCloudData.Add(pointCloudMapping.x, overrideData);
                    }

                    float4 originalPosRadius = m_DataEntry[i].originalPosition.xyzz;
                    originalPosRadius.w = m_OmissionArea;

                    overrideData.entries.Add(m_DataEntry[i]);
                    overrideData.originalPosRadius.Add(originalPosRadius);
                    
                    ++numberOfChanges;
                }
            }
            
            Debug.Log($"Writing {numberOfChanges} changed pointcloud entries");

            foreach (var overrideData in overridePerPointCloudData)
            {
                PointCloudFromHoudiniAsset.PointCloudOverrideData overrideDataDst = new PointCloudFromHoudiniAsset.PointCloudOverrideData()
                {
                    originalPositionRadius = overrideData.Value.originalPosRadius.ToArray(),
                    overrideData = overrideData.Value.entries.ToArray()
                };

                var dataIndex = overrideData.Key;

                var assetAndRelativeDataIndex = m_PointCloudDataIndexToAssetAndIndexMapping[dataIndex];
                
                assetAndRelativeDataIndex.Item1.AddOverrideData(assetAndRelativeDataIndex.Item2, overrideDataDst);
                assetsModified.Add(assetAndRelativeDataIndex.Item1);
            }


            foreach (var asset in assetsModified)
            {
                asset.ApplyOverrideData();
            }
        }
        
        private void GatherPotentialChangesFromInstances()
        {
            for (int i = 0; i < m_Instances.Length; ++i)
            {
                RecordPotentialChanges(i);
            }
        }
        
        private void RecordPotentialChanges(int index)
        {
            var instance = m_Instances[index];
            if (instance.scatterData == null) return;
            if (instance.scatterData.HasChanges)
            {
                m_DataEntry[index] = instance.scatterData.GetChangedDataEntry();
                m_HasChangedDataEntry[index] = true;
                
            }
        }
        
        private Instance Instantiate(PointCloudUtility.PointCloudDataEntry pc, int index)
        {
            float3 pos = pc.pcData.positions[index];
            float4 rotation = pc.pcData.rotations[index];
            float scale = pc.pcData.scales[index];
            var prefab = pc.pcData.prefab;

            
            float age = pc.pcData.HasAge(index) ? pc.pcData.age[index] : 0;
            float health = pc.pcData.HasHealth(index) ? pc.pcData.health[index] : 0;
            Color32 col = pc.pcData.HasColor(index) ? pc.pcData.color[index] : default;
            uint partIndex = pc.pcData.HasPartIndex(index) ? pc.pcData.partIndices[index] : default;

            var go = Object.Instantiate(prefab);
            var extraDataOverride = go.AddComponent<ScatterDataOverride>();

            extraDataOverride.Setup(pos, rotation, scale, age, health, (int)partIndex, col);
            
            return new Instance()
            {
                go = go,
                scatterData = extraDataOverride
            };
        }
        
        private void ReleaseResources()
        {
            m_PointCloudEntries = null;
            m_PartitionOutput.Dispose();
            
            unsafe
            {
                UnsafeUtility.Free(m_PointsToLoadCount.Counter, Allocator.Persistent);
                UnsafeUtility.Free(m_PointsToUnloadCount.Counter, Allocator.Persistent);
            }
            
            m_HasChangedDataEntry = null;
            m_DataEntry = null;
            m_Instances = null;
            m_PointCloudDataIndexToAssetAndIndexMapping = null;
        }

        private void RegisterForUpdates(bool register)
        {
            if (register)
            {
                UpdateModifyScatterToolMonoBehavior.GetInstance().OnUpdate += Tick;
            }
            else
            {
                UpdateModifyScatterToolMonoBehavior.GetInstance().OnUpdate -= Tick;
            }
            
        }


        JobHandle CalculatePointsToLoadAndUnload(float3 center, float radius, JobHandle deps)
        {
            var partitioningInfo = m_PartitionOutput.partitioningInfo;

            float2 min = center.xz - radius;
            float2 max = center.xz + radius;

            NativeHashSet<int> cellsToProcess = new NativeHashSet<int>(64, Allocator.Temp);

            for (float x = min.x; x <= max.x; x += partitioningInfo.cellSize)
            {
                for (float y = min.y; y <= max.y; y += partitioningInfo.cellSize)
                {
                    var index = partitioningInfo.GetFlatCellIndex(new float3(x, 0.0f, y));
                    cellsToProcess.Add(index);
                }
            }
            var activeCellsNow = cellsToProcess.ToNativeArray(Allocator.Temp);
            foreach (var lastIndices in m_LastCellIndicesInside)
            {
                cellsToProcess.Add(lastIndices);
            }
            m_LastCellIndicesInside.Clear();
            foreach (var cell in activeCellsNow)
            {
                m_LastCellIndicesInside.Add(cell);
            }
            activeCellsNow.Dispose();

            //schedule jobs to process cells
            m_PointsToLoadCount.Reset();
            m_PointsToUnloadCount.Reset();

            JobHandle handle = deps;
            foreach (var cell in cellsToProcess)
            {
                handle = new CalculatePointsToProcess()
                {
                    Center = center,
                    RadiusSqr = radius * radius,
                    CellOffsetToIndexMapping = m_PartitionOutput.offsetPerCell[cell],
                    CellToIndexMapping = m_PartitionOutput.cellToIndexMapping,
                    LoadCounter = m_PointsToLoadCount,
                    PointsToLoad = m_PointsToLoadIndices,
                    UnloadCounter = m_PointsToUnloadCount,
                    PointsToUnload = m_PointsToUnloadIndices,
                    PointsLoaded = m_PerPointLoadStatus,
                    Positions = m_PartitionOutput.transformedPositions
                }.Schedule(m_PartitionOutput.numberOfEntriesPerCell[cell], 64, handle);
            }

            return handle;
        }
        
        
        [BurstCompile]
        private struct CalculatePointsToProcess : IJobParallelFor
        {
            public float3 Center;
            public float RadiusSqr;
            
            public int CellOffsetToIndexMapping;

            [NativeDisableUnsafePtrRestriction] 
            public UnsafeAtomicCounter32 LoadCounter;
            [NativeDisableParallelForRestriction] 
            public NativeArray<int> PointsToLoad;
            [NativeDisableUnsafePtrRestriction] 
            public UnsafeAtomicCounter32 UnloadCounter;
            [NativeDisableParallelForRestriction] 
            public NativeArray<int> PointsToUnload;
            [NativeDisableParallelForRestriction] 
            public NativeArray<int> PointsLoaded;
            
            [ReadOnly] 
            public NativeArray<float3> Positions;
            [ReadOnly]
            public NativeArray<int> CellToIndexMapping;
            

            public void Execute(int index)
            {
                int pointIndex = CellToIndexMapping[CellOffsetToIndexMapping + index];
                
                bool isLoaded = PointsLoaded[pointIndex] > 0;
                
                float3 pointPosition = Positions[pointIndex];

                float3 toCenter = pointPosition - Center;

                bool isOutside = math.dot(toCenter, toCenter) > RadiusSqr;

                if (isLoaded == isOutside)
                {
                    if (isOutside)
                    {
                        int unloadIndex = UnloadCounter.Add(1);
                        PointsToUnload[unloadIndex] = pointIndex;
                        PointsLoaded[pointIndex] = 0;
                    }
                    else
                    {
                        int loadIndex = LoadCounter.Add(1);
                        PointsToLoad[loadIndex] = pointIndex;
                        PointsLoaded[pointIndex] = 1;
                    }
                }

            }
        }
        
    }
}