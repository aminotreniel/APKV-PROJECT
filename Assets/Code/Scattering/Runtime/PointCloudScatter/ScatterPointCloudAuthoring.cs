
using System;
using UnityEngine;

namespace TimeGhost
{
    public class ScatterPointCloudAuthoring : MonoBehaviour
    {
        public PointCloudFromHoudiniAsset pointCloudAsset;
        public float scatterActiveDistanceMin = 0.0f;
        public float scatterActiveDistanceMax = 100.0f;
        public float scatterSceneSectionSize = 180;
        public float scatterTileSize = 30;
    }
}