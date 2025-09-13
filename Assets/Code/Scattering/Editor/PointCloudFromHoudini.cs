using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace TimeGhost
{
    public static class PointCloudFromHoudini
    {
        public static string[] s_PcPrefixes = { "scatter_pcache_", "scatter_pcache_vegetation_" };
        public static string s_PrefabPostfix = "_pc_prefab";
        public static string s_MeshPrefix = "mesh_LOD";

        public static int LoadPointCloudData(Object pointCloud, ref float3[] positionsOut, ref float4[] orientationsOut, ref float[] scalesOut,
            ref float[] ageOut, ref float[] healthOut, ref Color32[] colorOut, ref uint[] partIndices)
        {
            string pcPath = AssetDatabase.GetAssetPath(pointCloud);
            if(string.IsNullOrEmpty(pcPath)) return 0;

            //set the point cache properties
            string[] lines = File.ReadAllLines(pcPath);
            int ptsCount = int.Parse(lines[3].Replace("elements ", ""));


            Object[] surfaces = AssetDatabase.LoadAllAssetsAtPath(pcPath);

            Texture2D positionsTex = null;
            Texture2D scalesTex = null;
            Texture2D orientationsTex = null;
            Texture2D ageTex = null;
            Texture2D healthTex = null;
            Texture2D colorTex = null;
            Texture2D partTex = null;

            foreach (Object tex in surfaces)
            {
                if (tex is Texture2D)
                {
                    if (tex.name == "pcPosition")
                    {
                        positionsTex = (Texture2D)tex;
                    }
                    else if (tex.name == "pcOrientation")
                    {
                        orientationsTex = (Texture2D)tex;
                    }
                    else if (tex.name == "pcScale")
                    {
                        scalesTex = (Texture2D)tex;
                    }
                    else if (tex.name == "pcAge")
                    {
                        ageTex = (Texture2D)tex;
                    }
                    else if (tex.name == "pcHealth")
                    {
                        healthTex = (Texture2D)tex;
                    }
                    else if (tex.name == "pcColor")
                    {
                        colorTex = (Texture2D)tex;
                    }
                    else if (tex.name == "pcPartId")
                    {
                        partTex = (Texture2D)tex;
                    }
                }
            }

            if (positionsTex != null && scalesTex != null && orientationsTex != null)
            {
                AppendPoints(ptsCount, positionsTex, orientationsTex, scalesTex, ageTex, healthTex, colorTex, partTex,
                    ref positionsOut, ref orientationsOut, ref scalesOut, ref ageOut,
                    ref healthOut, ref colorOut, ref partIndices);
                return ptsCount;
            }

            return 0;
        }

        public static int CreateMaskFromExtraData(int requiredEntryCount, float[] age, float[] health, Color32[] color, uint[] partIndices)
        {
            int mask = 0;
            if (age != null && health != null && age.Length == requiredEntryCount && health.Length == requiredEntryCount)
            {
                mask |= 1 << ExtraDataUtils.GetExtraDataIndex(ScatterExtraData.AgeHealth);
            }
            
            if (color != null && color.Length == requiredEntryCount)
            {
                mask |= 1 << ExtraDataUtils.GetExtraDataIndex(ScatterExtraData.Color);
            }
            
            if (partIndices != null && partIndices.Length == requiredEntryCount)
            {
                mask |= 1 << ExtraDataUtils.GetExtraDataIndex(ScatterExtraData.PartIndex);
            }

            return mask;
        }
        
        public static bool CreateExtraData(int requiredEntryCount, float[] age, float[] health, Color32[] color, uint[] partIndices, out NativeArray<byte> extraDataOut, out int maskOut)
        {
            int mask = CreateMaskFromExtraData(requiredEntryCount, age, health, color, partIndices);
            maskOut = mask;
            
            if (mask == 0)
            {
                extraDataOut = default;
                return false;
            }

            int stride = ExtraDataUtils.GetExtraDataStride(mask);

            extraDataOut = new NativeArray<byte>(stride * requiredEntryCount, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            int ageOffset = ExtraDataUtils.GetExtraDataOffsetFromMaskIfPresent(mask, ExtraDataUtils.GetExtraDataIndex(ScatterExtraData.AgeHealth));
            int colorOffset = ExtraDataUtils.GetExtraDataOffsetFromMaskIfPresent(mask, ExtraDataUtils.GetExtraDataIndex(ScatterExtraData.Color));
            int partIndexOffset = ExtraDataUtils.GetExtraDataOffsetFromMaskIfPresent(mask, ExtraDataUtils.GetExtraDataIndex(ScatterExtraData.PartIndex));
            unsafe
            {
                byte* ptr = (byte*)extraDataOut.GetUnsafePtr();
                for (int i = 0; i < requiredEntryCount; ++i)
                {
                    if (ageOffset != -1)
                    {
                        int offset = stride * i + ageOffset;
                        float2 data = new float2(age[i], health[i]);
                        UnsafeUtility.MemCpy(ptr + offset, &data, ExtraDataUtils.GetExtraDataSize(ScatterExtraData.AgeHealth));
                    }
                    if (colorOffset != -1)
                    {
                        int offset = stride * i + colorOffset;
                        Color32 data = color[i];
                        UnsafeUtility.MemCpy(ptr + offset, &data, ExtraDataUtils.GetExtraDataSize(ScatterExtraData.Color));
                    }
                    
                    if (partIndexOffset != -1)
                    {
                        int offset = stride * i + partIndexOffset;
                        uint data = partIndices[i];
                        UnsafeUtility.MemCpy(ptr + offset, &data, ExtraDataUtils.GetExtraDataSize(ScatterExtraData.PartIndex));
                    }
                }
            }
            
            
            return true;
        }

        public static string GetOrCreatePrefabForPointCache(Object pc)
        {
            string[] guids = TryToFindModelsForPointCache(pc, true);
            if (guids == null || guids.Length == 0)
            {
                string[] modelsGuids = TryToFindModelsForPointCache(pc, false);
                if (modelsGuids != null && modelsGuids.Length > 0)
                {
                    string modelGuid = modelsGuids[0];
                    string modelPath = AssetDatabase.GUIDToAssetPath(modelGuid);
                    GameObject model = AssetDatabase.LoadAssetAtPath<GameObject>(modelPath);
                    var newPath = Path.Combine(Path.GetDirectoryName(modelPath), Path.GetFileNameWithoutExtension(modelPath) + s_PrefabPostfix + ".prefab");
                    PrefabUtility.SaveAsPrefabAsset(model, newPath);
                    Debug.Log("Automatically creating a prefab for pointcloud, new prefab = " + newPath);
                    guids = TryToFindModelsForPointCache(pc, true);
                }
            }

            if (guids == null || guids.Length == 0)
            {
                Debug.LogError("Couldn't find a prefab for PointCloud " + pc.name);
                return null;
            }

            if (guids.Length > 1)
            {
                var guid =  FilterGUIDs(guids);
                Debug.LogWarning("Multiple prefabs have been found for Point Cache: " + pc.name + "! The implementation will pick one");
                return guid;
            }
                

            return guids[0];
        }

        public static string FilterGUIDs(string[] guids)
        {
            if (guids == null || guids.Length == 0) return null;
            if (guids.Length == 1) return guids[0];

            int bestIndex = 0;
            int difference = int.MaxValue;
            for (int i = 0; i < guids.Length; ++i)
            {
                var fileName = Path.GetFileNameWithoutExtension(AssetDatabase.GUIDToAssetPath(guids[i]));
                foreach (var pcPrefix in s_PcPrefixes)
                {
                    string assetName = fileName;
                    assetName = assetName.Replace(pcPrefix, "");
                    fileName.Replace(assetName, "");
                    if (fileName.Length < difference)
                    {
                        bestIndex = i;
                        difference = fileName.Length;
                    }
                }
            }

            return guids[bestIndex];
        }

        public static string[] TryToFindModelsForPointCache(Object pc, bool findPrefab)
        {
            string[] guids = null;
            foreach (var pcPrefix in s_PcPrefixes)
            {
                string assetName = pc.name;
                assetName = assetName.Replace(pcPrefix, "");
                if (findPrefab)
                {
                    assetName += s_PrefabPostfix;
                }

                var guidPrefabs = AssetDatabase.FindAssets(assetName + (findPrefab ? " t: prefab" : " t: model"), new[] { "Assets" });

                if (guidPrefabs.Length > 0)
                {
                    guids = guidPrefabs;
                    Debug.Log($"Found Pointcloud Asset {assetName} for pointcloud {pc.name}");
                    break;
                }
            }

            if (guids == null || guids.Length == 0)
            {
                Debug.Log("No prefab/model found for: " + pc.name + ", Ignoring.");
                return null;
            }

            return guids;
        }

        static Color32 ConvertFromHalf(half4 col)
        {
            return new Color(col.x, col.y, col.z, col.w);
        }

        static void AppendPoints(int pointCount, Texture2D positions, Texture2D orientations, Texture2D scales, Texture2D ages,
            Texture2D health, Texture2D color,Texture2D partIndices, ref float3[] positionsOut, ref float4[] orientationsOut, ref float[] scalesOut,
            ref float[] ageOut, ref float[] healthOut, ref Color32[] colorOut, ref uint[] partsOut)
        {
            if (positions.format == TextureFormat.RGBAHalf)
            {
                AppendDataFromTexture(pointCount, positions, ref positionsOut, (half4 texData) => new float3(texData.x, texData.y, texData.z));
            }
            else
            {
                AppendDataFromTexture(pointCount, positions, ref positionsOut, (float4 texData) => new float3(texData.x, texData.y, texData.z));
            }
            
            
            AppendDataFromTexture(pointCount, orientations, ref orientationsOut, (half4 texData) => { var quat = Quaternion.Euler(texData.x, texData.y, texData.z); return new float4(quat.x, quat.y, quat.z, quat.w); } );
            AppendDataFromTexture(pointCount, scales, ref scalesOut, (half texData) => texData);
            AppendDataFromTexture(pointCount, ages, ref ageOut, (half texData) => texData);
            AppendDataFromTexture(pointCount, health, ref healthOut, (half texData) => texData);
            AppendDataFromTexture(pointCount, color, ref colorOut, (half4 texData) => ConvertFromHalf(texData));
            AppendDataFromTexture(pointCount, partIndices, ref partsOut, (half texData) => (uint)(float)texData);
        }

        static bool AppendDataFromTexture<TOutputDataType, TTextureDataFormat>(int numberOfEntries, Texture2D dataSrc, ref TOutputDataType[] output, Func<TTextureDataFormat, TOutputDataType> converter) where TTextureDataFormat : struct
        {
            if (dataSrc == null) return false;

            var previousSize = output == null ? 0 : output.Length;
            ResizeChecked(ref output, previousSize + numberOfEntries);

            NativeArray<TTextureDataFormat> dataArray = dataSrc != null ? dataSrc.GetPixelData<TTextureDataFormat>(0) : default;

            for (int i = 0; i < numberOfEntries; ++i)
            {
                int dstIndex = previousSize + i;
                int srcIndex = i;

                output[dstIndex] = converter(dataArray[srcIndex]);
            }

            return true;
        }

        static void AppendDataFromTextureOrDefault<TOutputDataType, TTextureDataFormat>(int numberOfEntries, Texture2D dataSrc, ref TOutputDataType[] output, Func<TTextureDataFormat, TOutputDataType> converter, TOutputDataType defaultValue) where TTextureDataFormat : struct
        {
            if (!AppendDataFromTexture(numberOfEntries, dataSrc, ref output, converter))
            {
                var previousSize = output == null ? 0 : output.Length;
                ResizeChecked(ref output, previousSize + numberOfEntries);
                
                for (int i = 0; i < numberOfEntries; ++i)
                {
                    int dstIndex = previousSize + i;
                    output[dstIndex] = defaultValue;
                }
            }
        }

        static void ResizeChecked<T>(ref T[] array, int length)
        {
            if (array == null)
                array = new T[length];
            if (array.Length != length)
                System.Array.Resize(ref array, length);
        }

       

    }
}