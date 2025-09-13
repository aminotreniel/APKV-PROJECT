using System;
using System.IO;
using System.Linq;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;
using UnityEngine;

namespace TimeGhost
{
    public class PointCloudAssetSerializer
    {
        struct PointCloudDataEntry
        {
            public long numberOfPoints;
            public long numberOfAges;
            public long numberOfHealths;
            public long numberOfColors;
            public long numberOfPartIndices;
        }
        
        struct PointCloudOverrideDataEntry
        {
            public long numberOfOverrides;
        }

        private readonly byte[] c_FileType = {(byte)'p',(byte)'c',(byte)'a', (byte)'s'};
        private readonly byte[] c_TypeIDPointCloudData = {(byte)'p',(byte)'c',(byte)'d'};
        private readonly byte[] c_TypeIDOverrideData = {(byte)'p',(byte)'c',(byte)'o', (byte)'d'};
        
        private Stream m_Stream;

        public PointCloudAssetSerializer(Stream stream)
        {
            m_Stream = stream;
        }

        public Writer GetWriter()
        {
            return new Writer(this);
        }
        
        public Reader GetReader()
        {
            return new Reader(this);
        }
        
        public class Writer
        {
            private PointCloudAssetSerializer m_Serializer;
            private BinaryWriter m_Writer;

            internal Writer(PointCloudAssetSerializer serializer)
            {
                m_Serializer = serializer;
                m_Writer = new BinaryWriter(serializer.m_Stream);
            }

            public void WriteMagic()
            {
                m_Writer.Write(m_Serializer.c_FileType);
            }

            public void Write(PointCloudFromHoudiniAsset.PointCloudOverrideData[] data)
            {
                int dataCount = data != null ? data.Length : 0;
                
                m_Writer.Write(m_Serializer.c_TypeIDOverrideData);
                Write(ref dataCount);
                
                for (int i = 0; i < dataCount; ++i)
                {
                    var entry = data[i];
                    Write(entry);
                }
            }
            
            public void Write(PointCloudFromHoudiniAsset.PointCloudData[] data)
            {
                int pcCount = data?.Length ?? 0;
            
                m_Writer.Write(m_Serializer.c_TypeIDPointCloudData);
                Write(ref pcCount);

                for (int i = 0; i < pcCount; ++i)
                {
                    PointCloudFromHoudiniAsset.PointCloudData pcData = data[i];
                    Write(pcData);
                }
            
            }
            
            private void Write(PointCloudFromHoudiniAsset.PointCloudOverrideData data)
            {
                PointCloudOverrideDataEntry header = new PointCloudOverrideDataEntry();
                if (!data.IsValid())
                {
                    header.numberOfOverrides = 0;
                }
                else
                {
                    header.numberOfOverrides = data.overrideData.Length;
                }
                    
                Write(ref header);
                Write(data.overrideData);
                Write(data.originalPositionRadius);
            }
                
            private void Write(PointCloudFromHoudiniAsset.PointCloudData data)
            {
                //header
                PointCloudDataEntry header = new PointCloudDataEntry();
                header.numberOfPoints = data.positions?.Length ?? 0;
                header.numberOfAges = data.age?.Length ?? 0;
                header.numberOfHealths = data.health?.Length ?? 0;;
                header.numberOfColors = data.color?.Length ?? 0;;
                header.numberOfPartIndices = data.partIndices?.Length ?? 0;;
                Write(ref header);
                
                //data
                Write(data.positions);
                Write(data.rotations);
                Write(data.scales);
                Write(data.age);
                Write(data.health);
                Write(data.color);
                Write(data.partIndices);
            
            }

            private unsafe void Write<T>(T[] data) where T: unmanaged
            {
                if (data == null) return;
                void* ptr = UnsafeUtility.PinGCArrayAndGetDataAddress(data, out var handle);
                Write(ptr, UnsafeUtility.SizeOf<T>() * data.Length);
                UnsafeUtility.ReleaseGCObject(handle);
            }

        
            private unsafe void Write<T>(ref T data) where T: unmanaged
            {
                void* ptr = UnsafeUtility.AddressOf(ref data);
                Write(ptr, UnsafeUtility.SizeOf<T>());
            }
        
            private unsafe void Write(void* data, int length)
            {
                var span = new Span<byte>(data, length);
                m_Writer.Write(span);
            }
        
            private unsafe void Write(byte* data, int length)
            {
                var span = new Span<byte>(data, length);
                m_Writer.Write(span);
            }
        }


        public class Reader
        {
            private PointCloudAssetSerializer m_Serializer;
            private BinaryReader m_Reader;

            internal Reader(PointCloudAssetSerializer serializer)
            {
                m_Serializer = serializer;
                m_Reader = new BinaryReader(m_Serializer.m_Stream);
            }

            bool ReadAndCheck(byte[] id)
            {
                byte[] comp = new byte[id.Length];
                if (!Read(comp)) return false;
                for (int i = 0; i < id.Length; ++i)
                {
                    if (comp[i] != id[i]) return false;
                }

                return true;
            }
            public bool CheckMagic()
            {
                return ReadAndCheck(m_Serializer.c_FileType);
            }
            
            public bool Read(ref PointCloudFromHoudiniAsset.PointCloudOverrideData[] data)
            {
                if (!ReadAndCheck(m_Serializer.c_TypeIDOverrideData)) return false;
                int dataCount = 0;
                if (!Read(ref dataCount)) return false;
                
                if (dataCount == 0)
                {
                    data = null;
                    return true;
                }

                data = new PointCloudFromHoudiniAsset.PointCloudOverrideData[dataCount];
                for (int i = 0; i < dataCount; ++i)
                {
                    if (!Read(ref data[i])) return false;
                }

                return true;
            }

            
            public bool Read(ref PointCloudFromHoudiniAsset.PointCloudData[] data)
            {
                if (!ReadAndCheck(m_Serializer.c_TypeIDPointCloudData)) return false;

                int pcCount = 0;
                if (!Read(ref pcCount)) return false;

                if (pcCount == 0)
                {
                    data = null;
                    return true;
                }
                
                data = new PointCloudFromHoudiniAsset.PointCloudData[pcCount];
                for (int i = 0; i < pcCount; ++i)
                {
                    if (!Read(ref data[i])) return false;
                }

                return true;
            }
            
            private bool Read(ref PointCloudFromHoudiniAsset.PointCloudOverrideData data)
            {
                PointCloudOverrideDataEntry header = new PointCloudOverrideDataEntry();
                if (!Read(ref header)) return false;

                if (header.numberOfOverrides > 0)
                {
                    data.overrideData = new PointCloudFromHoudiniAsset.OverridePointCloudEntry[header.numberOfOverrides];
                    data.originalPositionRadius = new float4[header.numberOfOverrides];
                    
                    if (!Read(data.overrideData)) return false;
                    if (!Read(data.originalPositionRadius)) return false;
                }
                
                return true;
            }

            
            private bool Read(ref PointCloudFromHoudiniAsset.PointCloudData data)
            {
                PointCloudDataEntry header = new PointCloudDataEntry();
                if (!Read(ref header)) return false;

                if (header.numberOfPoints > 0)
                {
                    data.positions = new float3[header.numberOfPoints];
                    data.rotations = new float4[header.numberOfPoints];
                    data.scales = new float[header.numberOfPoints];

                    if (!Read(data.positions)) return false;
                    if (!Read(data.rotations)) return false;
                    if (!Read(data.scales)) return false;
                }

                if (header.numberOfAges > 0)
                {
                    data.age = new float[header.numberOfAges];
                    if (!Read(data.age)) return false;
                }
                
                if (header.numberOfHealths > 0)
                {
                    data.health = new float[header.numberOfHealths];
                    if (!Read(data.health)) return false;
                }
                
                if (header.numberOfColors > 0)
                {
                    data.color = new Color32[header.numberOfColors];
                    if (!Read(data.color)) return false;
                }
                
                if (header.numberOfPartIndices > 0)
                {
                    data.partIndices = new uint[header.numberOfPartIndices];
                    if (!Read(data.partIndices)) return false;
                }

                return true;
            }

            private unsafe bool Read<T>(T[] data) where T: unmanaged
            {
                if (data == null) return true;
                void* ptr = UnsafeUtility.PinGCArrayAndGetDataAddress(data, out var handle);
                bool val = Read(ptr, UnsafeUtility.SizeOf<T>() * data.Length);
                UnsafeUtility.ReleaseGCObject(handle);
                return val;
            }
        
            private unsafe bool Read<T>(ref T data) where T: unmanaged
            {
                void* ptr = UnsafeUtility.AddressOf(ref data);
                bool val = Read(ptr, UnsafeUtility.SizeOf<T>());
                return val;
            }
        
            private unsafe bool Read(void* data, int length) 
            {
                var span = new Span<byte>(data, length);
                var read = m_Reader.Read(span);
                return read == length;
            }

        
            private unsafe bool Read(byte* data, int length)
            {
                var span = new Span<byte>(data, length);
                var read = m_Reader.Read(span);
                return read == length;
            }
        }
    }
}