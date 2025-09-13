using System.Threading;
using Unity.Collections;
using Unity.Entities;
using Unity.Transforms;
using UnityEngine;
using Hash128 = Unity.Entities.Hash128;

namespace TimeGhost
{
    
    public static class InterlockedExtension
    {
        public static void InterlockedMax(ref float target, float value)
        {
            float currentValue;
            do
            {
                currentValue = target;
            } while ((value > currentValue) && Interlocked.CompareExchange(ref target, value, currentValue) != currentValue);

        }

        public static void InterlockedMin(ref float target, float value)
        {
            float currentValue;
            do
            {
                currentValue = target;
            } while ((value < currentValue) && Interlocked.CompareExchange(ref target, value, currentValue) != currentValue);
        }
    }
    public static class CommonScatterUtilities
    {
        public static void Resize<T>(ref NativeArray<T> array, int size, Allocator alloc, NativeArrayOptions opt = NativeArrayOptions.UninitializedMemory) where T : unmanaged
        {
            if (!array.IsCreated || array.Length < size)
            {
                if (array.IsCreated)
                    array.Dispose();

                array = new NativeArray<T>(size, alloc, opt);
            }
        }

        public static bool IsPointCloudDataValid(in PointCloudFromHoudiniAsset.PointCloudData pointCloudData)
        {
            return pointCloudData.positions != null && pointCloudData.positions.Length != 0 && pointCloudData.prefab != null;
        }

        public static Hash128 CalculateGroupPointCloudIdentifier(ScatterPointCloudAuthoring pointCloudAuthoring)
        {
            var groupId = default(UnityEngine.Hash128);

            for (uint pcIndex = 0, pcCount = (uint)pointCloudAuthoring.pointCloudAsset.GetPointCloudData().Length, entityIndex = 0; pcIndex < pcCount; ++pcIndex)
            {
                if (CalculatePointCloudIdentifier(pointCloudAuthoring, pcIndex, entityIndex, out var id))
                {
                    groupId.Append(ref id.Value);
                    ++entityIndex;
                }
            }

            return groupId;
        }

        public static bool CalculatePointCloudIdentifier(ScatterPointCloudAuthoring pointCloudAuthoring, uint pointCloudDataIndex, uint entityIndex, out Hash128 identifier)
        {
            ref readonly var pointCloudData = ref pointCloudAuthoring.pointCloudAsset.GetPointCloudData()[pointCloudDataIndex];

            if (!IsPointCloudDataValid(pointCloudData))
            {
                identifier = default;
                return false;
            }

            // We need to separate incremental bakes from previous bakes using some kind of generational salt value in
            // the hash. Originally, this was DateTime.UtcNow.GetHashCode(), however we also need the hash to be consistent
            // across the entire bake from multiple, unrelated bakers. As a compromise we're now using Time.frameCount
            // which, while not perfect, hopefully suffices as a per-tick generational increment (it's virtually impossible
            // to interactively change anything in Unity UI without triggering an engine tick).
            var salt = (uint)Time.frameCount;

            var id = new UnityEngine.Hash128(0, (uint)pointCloudAuthoring.GetHashCode(), entityIndex, salt);
            id.Append(pointCloudData.positions);
            id.Append(pointCloudData.rotations);
            id.Append(pointCloudData.scales);
            if (pointCloudData.age != null)
            {
                id.Append(pointCloudData.age);
            }
            if (pointCloudData.health != null)
            {
                id.Append(pointCloudData.health);
            }
            if (pointCloudData.color != null)
            {
                id.Append(pointCloudData.color);
            }
            if (pointCloudData.partIndices != null)
            {
                id.Append(pointCloudData.partIndices);
            }
            id.Append(pointCloudData.prefab.GetHashCode());
            id.Append(pointCloudAuthoring.transform.GetHashCode());

            identifier = id;
            return true;
        }
    }
}