using System;
using System.Collections.Generic;
using System.IO;
using Unity.DemoTeam.DigitalHuman;
using Unity.Mathematics;
using UnityEditor;
using UnityEngine;

namespace TimeGhost
{
    public static class PointCloudFromHoudiniAssetExtensions
    {
        public static void AddOverrideData(this PointCloudFromHoudiniAsset asset, int pointCloudDataIndex, PointCloudFromHoudiniAsset.PointCloudOverrideData overrideData)
        {
            PointCloudFromHoudiniAsset.PointCloudOverrideData overrideDst = asset.GetOverrideData(pointCloudDataIndex);
            
            if (overrideDst.IsValid())
            {
                MergeOverrideData(ref overrideDst, ref overrideData);
            }
            else
            {
                overrideDst = overrideData;
            }
            
            asset.SetOverrideData(pointCloudDataIndex, overrideDst);
        }
        
        public static void MergeOverrideData(ref PointCloudFromHoudiniAsset.PointCloudOverrideData dst, ref PointCloudFromHoudiniAsset.PointCloudOverrideData src)
        {
            Vector3[] previousOverrideDestinationPositions = new Vector3[dst.overrideData.Length];
            for (int i = 0; i < dst.overrideData.Length; i++)
            {
                previousOverrideDestinationPositions[i] = dst.overrideData[i].position;
            }

            Vector3[] currentOverrideSourcePositions = new Vector3[src.originalPositionRadius.Length];
            int[] closestPointIndices = new int[src.originalPositionRadius.Length];
            for (int i = 0; i < src.originalPositionRadius.Length; i++)
            {
                currentOverrideSourcePositions[i] = src.originalPositionRadius[i].xyz;
            }
            
            KdTree3 previousOverrides = new KdTree3(previousOverrideDestinationPositions, previousOverrideDestinationPositions.Length);

            unsafe
            {
                fixed(Vector3* sourcePositions = currentOverrideSourcePositions)
                fixed (int* closestIndices = closestPointIndices)
                {
                    previousOverrides.FindNearestForPointsJob(sourcePositions, closestIndices, currentOverrideSourcePositions.Length);
                }
            }

            List<PointCloudFromHoudiniAsset.OverridePointCloudEntry> newOverrides = new List<PointCloudFromHoudiniAsset.OverridePointCloudEntry>(64);
            List<float4> newOriginalPoints = new List<float4>(64);

            for (int i = 0; i < dst.overrideData.Length; i++)
            {
                newOverrides.Add(dst.overrideData[i]);
                newOriginalPoints.Add(dst.originalPositionRadius[i]);
            }

            for (int i = 0; i < src.originalPositionRadius.Length; i++)
            {
                int dstPointIndex = closestPointIndices[i];
                int srcPointIndex = i;
                float3 pos = dst.overrideData[dstPointIndex].position;
                float4 posRadius = src.originalPositionRadius[srcPointIndex];
                float3 delta = pos - posRadius.xyz;

                if (math.dot(delta, delta) < posRadius.w * posRadius.w)
                {
                    newOverrides[dstPointIndex] = src.overrideData[srcPointIndex];
                }
                else
                {
                    newOverrides.Add(src.overrideData[srcPointIndex]);
                    newOriginalPoints.Add(posRadius);
                }
            }

            dst.overrideData = newOverrides.ToArray();
            dst.originalPositionRadius = newOriginalPoints.ToArray();
        }
        
        public static void ApplyOverrideData(this PointCloudFromHoudiniAsset asset)
        { 
            PointCloudFromHoudiniAsset.PointCloudData[] originalDataArray = asset.GetUnmodifiedPointCloudData();
            PointCloudFromHoudiniAsset.PointCloudData[] modifiedDataArray = new PointCloudFromHoudiniAsset.PointCloudData[originalDataArray.Length];
            for (int i = 0; i < originalDataArray.Length; ++i)
            {
                PointCloudFromHoudiniAsset.PointCloudOverrideData overrideData = asset.GetOverrideData(i);
                modifiedDataArray[i] = originalDataArray[i];
                if (overrideData.IsValid())
                {
                    ApplyOverrides(ref modifiedDataArray[i], overrideData);
                }
            }
            
            asset.SetModifiedPointCloudData(modifiedDataArray);
            asset.Serialize();
            asset.ForceRebake();
        }

        
        public static void Serialize(this PointCloudFromHoudiniAsset asset)
        {
            var serializedData = asset.SerializeToByteArray();
            
            if (asset.m_BinaryData != null)
            {

                AssetDatabase.DeleteAsset(AssetDatabase.GetAssetPath(asset.m_BinaryData));
                
            }

            asset.m_BinaryData = null;
            
            if (serializedData != null)
            {
                asset.m_BinaryData = CreateBinaryFile(asset, serializedData);
            }
            
            EditorUtility.SetDirty(asset);
            Undo.ClearUndo(asset);
            AssetDatabase.SaveAssetIfDirty(asset);

        }

        static TextAsset CreateBinaryFile(PointCloudFromHoudiniAsset asset, byte[] data)
        {
            var houdiniAssetPath = AssetDatabase.GetAssetPath(asset);
            string binaryPrefix = $"_data{0}";
            var filename = Path.GetFileNameWithoutExtension(houdiniAssetPath) + binaryPrefix;
            var textAssetPath = Path.Combine(Path.GetDirectoryName(houdiniAssetPath), filename) + ".bytes";
            
            if (AssetDatabase.AssetPathExists(textAssetPath))
            {
                AssetDatabase.DeleteAsset(textAssetPath);
            }
            
            File.WriteAllBytes(Path.GetFullPath(textAssetPath), data);
            AssetDatabase.ImportAsset(textAssetPath, ImportAssetOptions.Default);
            TextAsset tAsset = AssetDatabase.LoadAssetAtPath<TextAsset>(textAssetPath);

            return tAsset;
        }

        static void ApplyOverrides(ref PointCloudFromHoudiniAsset.PointCloudData pointCloud, PointCloudFromHoudiniAsset.PointCloudOverrideData overrideData)
        {
            Vector3[] overrideOriginalPositions = new Vector3[overrideData.originalPositionRadius.Length];
            for (int i = 0; i < overrideOriginalPositions.Length; i++)
            {
                overrideOriginalPositions[i] = overrideData.originalPositionRadius[i].xyz;
            }
            KdTree3 previousOverrides = new KdTree3(overrideOriginalPositions, overrideOriginalPositions.Length);
            
            
            int[] closestPointIndices = new int[pointCloud.positions.Length];
            Vector3[] pointCloudPositions = new Vector3[pointCloud.positions.Length];
            for (int i = 0; i < pointCloudPositions.Length; ++i)
            {
                pointCloudPositions[i] = pointCloud.positions[i];
            }
            
            unsafe
            {
                fixed(Vector3* positions = pointCloudPositions)
                fixed (int* closestIndices = closestPointIndices)
                {
                    previousOverrides.FindNearestForPointsJob(positions, closestIndices, closestPointIndices.Length);
                }
            } 
            List<float3> newPositions = new List<float3>(pointCloudPositions.Length);
            List<float4> newRotations = new List<float4>(pointCloudPositions.Length);
            List<float> newScales = new List<float>(pointCloudPositions.Length);
            List<float> newAge = new List<float>(pointCloudPositions.Length);
            List<float> newHealth = new List<float>(pointCloudPositions.Length);
            List<Color32> newColor = new List<Color32>(pointCloudPositions.Length);
            List<uint> newPartIndices = new List<uint>(pointCloudPositions.Length);

            bool hasAge = pointCloud.age != null && pointCloud.age.Length == pointCloud.positions.Length;
            bool hasHealth = pointCloud.health != null && pointCloud.health.Length == pointCloud.positions.Length;
            bool hasColor = pointCloud.color != null && pointCloud.color.Length == pointCloud.positions.Length;
            bool partIndex = pointCloud.partIndices != null && pointCloud.partIndices.Length == pointCloud.positions.Length;

            for (int i = 0; i < closestPointIndices.Length; ++i)
            {
                int dstPointIndex = i;
                int srcPointIndex = closestPointIndices[i];
                float3 pos = pointCloudPositions[dstPointIndex];
                float4 posRadius = overrideData.originalPositionRadius[srcPointIndex];
                float3 delta = pos - posRadius.xyz;

                if (math.dot(delta, delta) >= posRadius.w * posRadius.w)
                {
                    newPositions.Add(pointCloud.positions[dstPointIndex]);
                    newRotations.Add(pointCloud.rotations[dstPointIndex]);
                    newScales.Add(pointCloud.scales[dstPointIndex]);
                    if (hasAge)
                    {
                        newAge.Add(pointCloud.age[dstPointIndex]);
                    }
                    
                    if (hasHealth)
                    {
                        newHealth.Add(pointCloud.health[dstPointIndex]);
                    }
                    
                    if (hasColor)
                    {
                        newColor.Add(pointCloud.color[dstPointIndex]);
                    }
                    
                    if (partIndex)
                    {
                        newPartIndices.Add(pointCloud.partIndices[dstPointIndex]);
                    }
                }
            }
            
            for (int i = 0; i < overrideData.overrideData.Length; ++i)
            {

                newPositions.Add(overrideData.overrideData[i].position);
                newRotations.Add(overrideData.overrideData[i].rotation);
                newScales.Add(overrideData.overrideData[i].scale);
                if (hasAge)
                {
                    newAge.Add(overrideData.overrideData[i].age);
                }
                    
                if (hasHealth)
                {
                    newHealth.Add(overrideData.overrideData[i].health);
                }
                    
                if (hasColor)
                {
                    Color32 col = new Color32();
                    col.FromInt(overrideData.overrideData[i].color);
                    newColor.Add(col);
                }
                    
                if (partIndex)
                {
                    newPartIndices.Add(overrideData.overrideData[i].partIndex);
                }
            }
            
            pointCloud.positions = newPositions.ToArray();
            pointCloud.rotations = newRotations.ToArray();
            pointCloud.scales = newScales.ToArray();
            pointCloud.age = hasAge ? newAge.ToArray() : null;
            pointCloud.health = hasHealth ? newHealth.ToArray() : null;
            pointCloud.color = hasColor ? newColor.ToArray() : null;
            pointCloud.partIndices = partIndex ? newPartIndices.ToArray() : null;
            
        }
        
    }
}