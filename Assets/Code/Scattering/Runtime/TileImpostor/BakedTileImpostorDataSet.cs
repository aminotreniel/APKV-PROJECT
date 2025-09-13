using System;
using System.Collections.Generic;
using System.Threading;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering.HighDefinition;
using UnityEngine.Serialization;

namespace TimeGhost
{
    [CreateAssetMenu(menuName = "BakedTileImpostorDataSet")]
    public class BakedTileImpostorDataSet : ScriptableObject
    {
        public enum TileImpostorType
        {
            Octahedral,
            TileMesh,
            TileCards
        }

        
        [Serializable]
        public struct GeneratedImposter
        {
            public int FlatTileIndex;
            public AABB ImpostorBounds;
            public GameObject Prefab;
        }

        [Serializable]
        public struct ImpostorBakeSource
        {
            [FormerlySerializedAs("asset")] public PointCloudFromHoudiniAsset Asset;
            [FormerlySerializedAs("transform")] public float4x4 Transform;
        }

        [Serializable]
        public struct ImpostorMeshDefinition
        {
            public float VerticesPerMeter;
            public float CardWidth;
            public bool UseCards;
        }
        
        public TileImpostorType ImpostorType = TileImpostorType.TileCards;

        public ImpostorMeshDefinition[] ImpostorMeshDefinitions = null;
        [FormerlySerializedAs("VerticesPerMeter")] public float VerticesPerMeter_Deprecated = 1;
        [FormerlySerializedAs("CardWidth")] public float CardWidth_Deprecated = 2;
        
        public int FrameResolution = 256;
        public int2 FrameCount = new int2(8, 8);
        public int DownSampleCount = 2;
        public float TileSize = 150;
        public float DiscardTileImpostorRatio = 0.2f;
        public float3 ExtraBoundsMargin = 0;
        public float HeightBias = 0;
        public bool UseAreaLimit = false;
        public AABB AreaLimit;
        public DiffusionProfileSettings OverrideDiffusionProfile;
        public ImpostorBakeSource[] Sources;
        public ScatterPointCloudSystem.PartitioningInfo PartitioningInfo;
        public GeneratedImposter[] GeneratedImposters;


    }
}
