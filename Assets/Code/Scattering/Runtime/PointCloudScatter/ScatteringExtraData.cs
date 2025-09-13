using System;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Unity.Burst;
using Unity.Collections;
using Unity.Entities;

using Unity.Mathematics;

using Unity.Collections.LowLevel.Unsafe;
using Unity.Properties;
using UnityEngine;
using Debug = UnityEngine.Debug;
using Hash128 = Unity.Entities.Hash128;

namespace TimeGhost
{
    public static class Extensions
    {
        public static int ToInt(this Color32 c)
        {
            int v = c.a << 24 | c.b << 16 | c.g << 8 | c.r;
            return v;
        }
        
        public static void FromInt(this Color32 c, int v)
        {
            c.r = (byte)(v & 0xFF);
            c.g = (byte)((v >> 8) & 0xFF);
            c.b = (byte)((v >> 16) & 0xFF);
            c.a = (byte)((v >> 24) & 0xFF);
        }
        
    }

    
    public struct ScatteredInstanceExtraData : IComponentData
    {
        public Hash128 ExtraDataHash;
        public int InstanceIndex;
    }
    
    public struct ScatteringExtraDataBlob
    {
        public int ExtraDataMask;
        public BlobArray<byte> Data;
    }
    public struct ScatteringExtraData : IComponentData
    {
        public Hash128 ExtraDataHash;
        public BlobAssetReference<ScatteringExtraDataBlob> ExtraData;
    }

    public enum ScatterExtraData
    {
        AgeHealth = 0,
        Color,
        PartIndex
    }
    [BurstCompile]
    public static class ExtraDataUtils
    {
        struct ExtraDataTypeSizes
        {
            public NativeArray<int> typeInfo;
            public int maxTypeIndex;
        }
        
        static readonly SharedStatic<ExtraDataTypeSizes> s_ExtraDataTypeInfo = SharedStatic<ExtraDataTypeSizes>.GetOrCreate<ExtraDataTypeSizes>();
        
#if UNITY_EDITOR
        [UnityEditor.InitializeOnLoadMethod]
        public static void EditorInitializeOnLoadMethod() => Init();
#else
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void RuntimeInitialization() => Init();
#endif
        
        static void Init()
        {
            int numberOfTypes = 3;
            var typeInfos = new NativeArray<int>(numberOfTypes, Allocator.Domain);
            typeInfos[0] = UnsafeUtility.SizeOf<float2>();
            typeInfos[1] = UnsafeUtility.SizeOf<Color32>();
            typeInfos[2] = UnsafeUtility.SizeOf<uint>();

            s_ExtraDataTypeInfo.Data.typeInfo = typeInfos;
            s_ExtraDataTypeInfo.Data.maxTypeIndex = numberOfTypes;
        }

        
        private static int RightmostSetBit(int n)
        {
            return (int)math.log2(n & -n);
        }

        public static int GetExtraDataIndex(ScatterExtraData type)
        {
            return (int)type;
        }

        public static int GetExtraDataSize(ScatterExtraData type)
        {
            return GetExtraDataSize((int)type);
        }
        
        public static int GetExtraDataSize(int typeIndex)
        {
            if (typeIndex >= s_ExtraDataTypeInfo.Data.maxTypeIndex) return 0;
            return s_ExtraDataTypeInfo.Data.typeInfo[typeIndex];
        }
        
        public static int GetExtraDataStride(int mask)
        {
            int stride = 0;
            while (mask != 0)
            {
                int index = RightmostSetBit(mask);
                if (index >= s_ExtraDataTypeInfo.Data.maxTypeIndex)
                {
                    Debug.LogError("malformed mask!");
                    return 0;
                }

                stride += GetExtraDataSize((ScatterExtraData)index);
                mask &= ~(1 << index);
            }
            return stride;
        }
        
        public static int GetExtraDataOffsetFromMask(int mask, int extraDataTypeIndex)
        {
            int m = (int)(0xFFFFFFFF ^ (0xFFFFFFFF << extraDataTypeIndex));

            return GetExtraDataStride(mask & m);
        }
        
        public static int GetExtraDataOffsetFromMaskIfPresent(int mask, int extraDataTypeIndex)
        {
            if ((mask & (1 << extraDataTypeIndex)) == 0) return -1; 
            return GetExtraDataOffsetFromMask(mask, extraDataTypeIndex);
        }

        public static bool HasExtraData(int mask, int extraDataTypeIndex)
        {
            return GetExtraDataOffsetFromMaskIfPresent(mask, extraDataTypeIndex) != -1;
        }
        

    }

    
    
}
