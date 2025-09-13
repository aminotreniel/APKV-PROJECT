using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Runtime.Serialization.Formatters.Binary;
using System.Text;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Serialization;
using Object = UnityEngine.Object;

namespace TimeGhost
{
    [CreateAssetMenu(menuName = "PointCloudFromHoudiniAsset")]
    public class PointCloudFromHoudiniAsset : ScriptableObject
    {
        [Serializable]
        public struct PointCloudData
        {
            [SerializeField] public float3[] positions;
            [SerializeField] public float4[] rotations;
            [SerializeField] public float[] scales;
            [SerializeField] public float[] age;
            [SerializeField] public float[] health;
            [SerializeField] public Color32[] color;
            [SerializeField] public uint[] partIndices;
            [SerializeField] public GameObject prefab;

            public bool HasAge(int index)
            {
                return age != null && age.Length > index;
            }
            public bool HasHealth(int index)
            {
                return health != null && health.Length > index;
            }
            public bool HasColor(int index)
            {
                return color != null && color.Length > index;
            }
            public bool HasPartIndex(int index)
            {
                return partIndices != null && partIndices.Length > index;
            }

            
        }

        [Serializable]
        public struct OverridePointCloudEntry
        {
            public float3 position;
            public float4 rotation;
            public float scale;
            public float age;
            public float health;
            public int color;
            public uint partIndex;
            
            public static implicit operator OverridePointCloudEntry(ScatterDataOverride.PointCloudOverrideDataEntry d)
            {

                return new OverridePointCloudEntry()
                {
                    position = d.position,
                    rotation = d.orientation.value,
                    scale = d.scale,
                    age = d.age,
                    health = d.health,
                    color = d.color.ToInt(),
                    partIndex = (uint)d.partIndex,
                };
            }
        }
            
        [Serializable]
        public struct PointCloudOverrideData
        {
            public OverridePointCloudEntry[] overrideData;
            public float4[] originalPositionRadius;

            public bool IsValid()
            {
                return overrideData != null && originalPositionRadius != null && overrideData.Length != 0 && originalPositionRadius.Length == overrideData.Length;
            }
        }
        
        static int[] Convert(Color32[] col)
        {
            if (col == null) return null;
            int[] arr = new int[col.Length];
            for (int i = 0; i < arr.Length; ++i)
            {
                arr[i] = col[i].ToInt();
            }
                
            return arr;
        }
            
        static Color32[] Convert(int[] col)
        {
            if (col == null) return null;
            Color32[] arr = new Color32[col.Length];
            for (int i = 0; i < arr.Length; ++i)
            {
                arr[i].FromInt(col[i]);
            }
                
            return arr;
        }

        
        /// <transition>
        [Serializable]
        internal struct PointCloudDataEntrySerialized
        {
            [SerializeField] public float3[] positions;
            [SerializeField] public float4[] rotations;
            [SerializeField] public float[] scales;
            [SerializeField] public float[] age;
            [SerializeField] public float[] health;
            [SerializeField] public int[] color;
            [SerializeField] public uint[] partIndices;

            static int[] Convert(Color32[] col)
            {
                if (col == null) return null;
                int[] arr = new int[col.Length];
                for (int i = 0; i < arr.Length; ++i)
                {
                    arr[i] = col[i].ToInt();
                }
                
                return arr;
            }
            
            static Color32[] Convert(int[] col)
            {
                if (col == null) return null;
                Color32[] arr = new Color32[col.Length];
                for (int i = 0; i < arr.Length; ++i)
                {
                    arr[i].FromInt(col[i]);
                }
                
                return arr;
            }
            
            public static implicit operator PointCloudDataEntrySerialized(PointCloudData e)
            {
                PointCloudDataEntrySerialized s;
                s.positions = e.positions;
                s.rotations = e.rotations;
                s.scales = e.scales;
                s.age = e.age;
                s.health = e.health;
                s.color = Convert(e.color);
                s.partIndices = e.partIndices;
                return s;
            }
            
            public static implicit operator PointCloudData(PointCloudDataEntrySerialized e)
            {
                PointCloudData s;
                s.positions = e.positions;
                s.rotations = e.rotations;
                s.scales = e.scales;
                s.age = e.age;
                s.health = e.health;
                s.color = Convert(e.color);
                s.partIndices = e.partIndices;
                s.prefab = null;
                return s;
            }
        }
        
        [Serializable]
        internal struct PointCloudDataSerialized
        {
            public PointCloudDataEntrySerialized[] PointCloudData;
            public PointCloudDataEntrySerialized[] ModifiedPointCloudData;
            public PointCloudOverrideData[] OverrideData;
            
            public static PointCloudDataEntrySerialized[] Convert(PointCloudData[] e)
            {
                PointCloudDataEntrySerialized[] s = new PointCloudDataEntrySerialized[e.Length];
                for (int i = 0; i < e.Length; ++i)
                {
                    s[i] = e[i];
                }
                return s;
            }
            
            public static PointCloudData[] Convert(PointCloudDataEntrySerialized[] e)
            {
                PointCloudData[] s = new PointCloudData[e.Length];
                for (int i = 0; i < e.Length; ++i)
                {
                    s[i] = e[i];
                }
                return s;
            }
        }
        /// </summary>
        

        [FormerlySerializedAs("pointCaches")]
        public Object[] m_PointCaches;
        [HideInInspector]
        public int m_ScatteredCount;

        public bool m_IgnoreMaxScatterDistance = true;
        
        //serialize only data (should only be used when serializing/deserialising)
        [SerializeField] 
        internal TextAsset m_BinaryData;
        [SerializeField] 
        internal GameObject[] m_Prefabs;
        
        //runtime
        private PointCloudData[] m_PointCloudData;
        private PointCloudData[] m_ModifiedPointCloudData;
        private PointCloudOverrideData[] m_OverrideData = null;

        private bool m_DataLoaded = false;

        
        public int TotalNumberOfInstances
        {
            get
            {
                var data = GetPointCloudData();
                if (data == null) return 0;
                int total = 0;
                foreach (var d in data)
                {
                    total += d.positions?.Length ?? 0;
                }

                return total;
            }
            
        }
        
        public void SetUnmodifiedPointCloudData(PointCloudData[] data)
        {
            m_PointCloudData = data;
        }
        
        public void SetModifiedPointCloudData(PointCloudData[] data)
        {
            m_ModifiedPointCloudData = data;
        }
        
        public PointCloudData[] GetPointCloudData()
        {
            EnsureDataLoaded();
            if (m_ModifiedPointCloudData != null && m_ModifiedPointCloudData.Length == m_PointCloudData.Length)
            {
                return m_ModifiedPointCloudData;
            }
            return m_PointCloudData;
        }

        public PointCloudData[] GetUnmodifiedPointCloudData()
        {
            
            EnsureDataLoaded();
            return m_PointCloudData;
        }

        public PointCloudOverrideData GetOverrideData(int pointCloudIndex)
        {
            EnsureDataLoaded();
            EnsureOverrideData();
            return m_OverrideData[pointCloudIndex];
        }

        public void SetOverrideData(int pointCloudIndex, in PointCloudOverrideData overrideData)
        {
            EnsureOverrideData();
            m_OverrideData[pointCloudIndex] = overrideData;
        }

        public void ClearOverrides()
        {
            EnsureDataLoaded();
            m_OverrideData = null;
            m_ModifiedPointCloudData = null;
        }
        
        public void ForceRebake()
        {
            m_ScatteredCount += 1;
        }

        public void ApplyPrefabArrayEntriesToPCData()
        {
            if (m_Prefabs == null || m_PointCloudData == null) return;
            for (int i = 0; i < math.min(m_Prefabs.Length, m_PointCloudData.Length); ++i)
            {
                m_PointCloudData[i].prefab = m_Prefabs[i];
            }
        }

        public byte[] SerializeToByteArray()
        {
            byte[] data = null;
            if (m_PointCloudData == null) return null;
            using (MemoryStream stream = new MemoryStream())
            {
                using (GZipStream compressionStream = new GZipStream(stream, CompressionMode.Compress))
                {
                    WriteToStream(compressionStream);
                }

                data = stream.ToArray();
            }
            
            m_Prefabs = new GameObject[m_PointCloudData.Length];
            
            for (int i = 0; i < m_PointCloudData.Length; ++i)
            {
                m_Prefabs[i] = m_PointCloudData[i].prefab;
            }

            return data;
        }

        public void Deserialize()
        {
            if (m_BinaryData == null)
            {
                Debug.LogError($"Couldn't deserialize {name}, binarydata not present.");
                return;
            }
            byte[] fullDataInBytes = m_BinaryData.bytes;
            using (MemoryStream stream = new MemoryStream(fullDataInBytes))
            {
                using (GZipStream compressionStream = new GZipStream(stream, CompressionMode.Decompress))
                {
                    ReadFromStream(compressionStream);
                }
                
                if (m_Prefabs != null)
                {
                    for (int i = 0; i < m_Prefabs.Length; ++i)
                    {
                        if (m_PointCloudData.Length > i)
                        {
                            m_PointCloudData[i].prefab = m_Prefabs[i];
                        }
                        if (m_ModifiedPointCloudData != null && m_ModifiedPointCloudData.Length > i)
                        {
                            m_ModifiedPointCloudData[i].prefab = m_Prefabs[i];
                        }
                    }
                }
                
            }

        }

        private void EnsureDataLoaded()
        {
            if (!m_DataLoaded)
            {
                Deserialize();
                m_DataLoaded = true;
            }
        }
        
        private void EnsureOverrideData()
        {
            if (m_OverrideData == null || m_OverrideData.Length != m_PointCloudData.Length)
            {
                var overrideData = new PointCloudOverrideData[m_PointCloudData.Length];
                if (m_OverrideData != null)
                {
                    m_OverrideData.CopyTo(overrideData, 0);
                }

                m_OverrideData = overrideData;
            }
        }

        private void WriteToStream(Stream memStream)
        {
            if (m_PointCloudData == null || m_PointCloudData.Length == 0) return;

            PointCloudAssetSerializer serializer = new PointCloudAssetSerializer(memStream);
            var writer = serializer.GetWriter();
            writer.WriteMagic();
            writer.Write(m_PointCloudData);
            writer.Write(m_ModifiedPointCloudData);
            writer.Write(m_OverrideData);
        }
        
        private void ReadFromStream(Stream memStream)
        {
            PointCloudAssetSerializer serializer = new PointCloudAssetSerializer(memStream);
            var reader = serializer.GetReader();
            if (reader.CheckMagic())
            {
                bool success;
                success = reader.Read(ref m_PointCloudData);
                success = success && reader.Read(ref m_ModifiedPointCloudData);
                success = success && reader.Read(ref m_OverrideData);

                if (!success)
                {
                    m_PointCloudData = null;
                    m_ModifiedPointCloudData = null;
                    m_OverrideData = null;
                    Debug.LogError($"Failed to deserialize {name}");
                }
            }
            else
            {
                memStream.Seek(0, SeekOrigin.Begin);
                var binaryReader = new BinaryFormatter();
            
                PointCloudDataSerialized data =  (PointCloudDataSerialized)binaryReader.Deserialize(memStream);
                m_PointCloudData = PointCloudDataSerialized.Convert(data.PointCloudData);
                m_ModifiedPointCloudData = PointCloudDataSerialized.Convert(data.ModifiedPointCloudData);
                m_OverrideData = data.OverrideData;
            }
            
           
        }

        

    }
}
