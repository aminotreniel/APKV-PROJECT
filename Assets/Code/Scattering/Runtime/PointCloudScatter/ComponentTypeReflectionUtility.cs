using System;
using System.Reflection;
using Unity.Entities;
using UnityEngine.Assertions;

namespace TimeGhost
{
    public struct ComponentTypeReflectionUtility
    {
        private Type[] m_EntitiesGraphicsInternalTypes;
        private DynamicComponentTypeHandle[] m_EntitiesGraphicsInternalTypeHandles;
        private bool m_Initialized;
        
        public void Init(EntityManager entManager, Assembly sourceAssembly, Type enumType)
        {
            if (m_Initialized) return;
            Assert.IsTrue(enumType.IsEnum);
            if (!enumType.IsEnum) return;
            
            string[] typeNames = Enum.GetNames(enumType);
            m_EntitiesGraphicsInternalTypes = new Type[typeNames.Length];
            m_EntitiesGraphicsInternalTypeHandles = new DynamicComponentTypeHandle[typeNames.Length];
            //load internal types we want to abuse
            var assembly = sourceAssembly;
            for (int i = 0; i < typeNames.Length; ++i)
            {
                var typeName = typeNames[i];
                m_EntitiesGraphicsInternalTypes[i] = assembly.GetType("Unity.Rendering." + typeName);
            }

            for (int i = 0; i < typeNames.Length; ++i)
            {
                var type = m_EntitiesGraphicsInternalTypes[i];
                m_EntitiesGraphicsInternalTypeHandles[i] = entManager.GetDynamicComponentTypeHandle(ComponentType.ReadWrite(type));
            }
            m_Initialized = true;
        }

        public void Cleanup()
        {

        }

        public ref DynamicComponentTypeHandle GetComponentHandle(int index)
        {
            return ref m_EntitiesGraphicsInternalTypeHandles[index];
        }
        
        public ref Type GetType(int index)
        {
            return ref m_EntitiesGraphicsInternalTypes[index];
        }
        
        public void UpdateDynamicTypeHandles(ref SystemState state)
        {
            for (int i = 0; i < m_EntitiesGraphicsInternalTypeHandles.Length; ++i)
            {
                m_EntitiesGraphicsInternalTypeHandles[i].Update(ref state);
            }
        }
        
    }
}