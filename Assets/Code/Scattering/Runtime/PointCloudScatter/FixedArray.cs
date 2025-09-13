using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;
using UnityEngine;

namespace TimeGhost
{

    
    //Adapted from fixed list from Collections. Difference is that it doesn't reserve the extra bytes for length 
    [Serializable]
    public struct FixedArray32Bytes<T> where T: unmanaged
    {
        [SerializeField]
        private FixedBytes32Align8 m_Data;
        
        internal readonly unsafe byte* AsPtr
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get
            {
                unsafe
                {
                    fixed (void* ptr = &m_Data)
                        return ((byte*)ptr);
                }
            }
        }
        
        public int Capacity
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => UnsafeUtility.SizeOf<FixedBytes32Align8>() / UnsafeUtility.SizeOf<T>();
        }

        public void Fill(in T val)
        {
            unsafe
            {
                fixed (T* ptr = &val)
                    UnsafeUtility.MemCpyReplicate(AsPtr, ptr, UnsafeUtility.SizeOf<T>(), Capacity);
            }
        }
        
        public T this[int index]
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            readonly get
            {
                unsafe
                {
                    return UnsafeUtility.ReadArrayElement<T>(AsPtr, index);
                }
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            set
            {
                unsafe
                {
                    UnsafeUtility.WriteArrayElement<T>(AsPtr, index, value);
                }
            }
        }
        
        public T[] ToArray()
        {
            var result = new T[Capacity];
            unsafe
            {
                byte* s = AsPtr;
                fixed(T* d = result)
                    UnsafeUtility.MemCpy(d, s, Capacity * UnsafeUtility.SizeOf<T>());
            }
            return result;
        }
    }
    
    [Serializable]
    [StructLayout(LayoutKind.Explicit, Size=16)]
    internal struct FixedBytes16Align8
    {
        /// <summary>
        /// For internal use only.
        /// </summary>
        [SerializeField] [FieldOffset(0)] public ulong byte0000;

        /// <summary>
        /// For internal use only.
        /// </summary>
        [SerializeField] [FieldOffset(8)] public ulong byte0008;

    }
    
    [Serializable]
    [StructLayout(LayoutKind.Explicit, Size=32)]
    internal struct FixedBytes32Align8
    {
        /// <summary>
        /// For internal use only.
        /// </summary>
        [SerializeField] [FieldOffset(0)] internal FixedBytes16Align8 offset0000;
        /// <summary>
        /// For internal use only.
        /// </summary>
        [SerializeField] [FieldOffset(16)] internal FixedBytes16Align8 offset0016;
    }
    

}