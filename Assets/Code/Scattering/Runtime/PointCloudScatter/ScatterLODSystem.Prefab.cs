using Unity.Assertions;
using Unity.Collections;
using Unity.Entities;
using Unity.Entities.Graphics;
using Unity.Mathematics;
using Unity.Rendering;
using UnityEngine;

namespace TimeGhost
{
    public partial class ScatterLODSystem
    {
        private enum TypesToReflect
        {
            LODWorldReferencePoint,
            LODGroupWorldReferencePoint,
            RootLODWorldReferencePoint,
            LODRange,
            RootLODRange
        }

        private static ComponentTypeReflectionUtility s_ReflectionUtility;

        void OnCreate_Prefabs()
        {
            s_ReflectionUtility.Init(EntityManager, typeof(EntitiesGraphicsSystem).Assembly, typeof(TypesToReflect));
        }

        void OnDestroy_Prefabs()
        {
            s_ReflectionUtility.Cleanup();
        }

        public static void TransformPrefabToScatterLOD(Entity loadedPrefab, EntityManager entMngr)
        {
            var root = RemoveLODsAndAddRenderComponentsToRoot(loadedPrefab, entMngr);
            if (root != Entity.Null)
            {
                entMngr.AddComponentData(root, new ScatterLODDebugColor() { ColorPacked = 0.0f });
            }
        }

        public static Entity RemoveLODsAndAddRenderComponentsToRoot(Entity loadedPrefab, EntityManager entMngr)
        {
            bool hasGraphicsRelevantComponent = false;

            bool needsMeshMatComponent = true;
            
            Entity prefabRoot = Entity.Null;
            if (entMngr.HasComponent<PrefabRoot>(loadedPrefab))
            {
                prefabRoot = entMngr.GetComponentData<PrefabRoot>(loadedPrefab).Root;
            }
            else
            {
                return prefabRoot;
            }
            
            if (entMngr.HasComponent<LinkedEntityGroup>(prefabRoot))
            {
                var dynBuff = entMngr.GetBuffer<LinkedEntityGroup>(prefabRoot, isReadOnly: false).Reinterpret<Entity>().AsNativeArray();
                var srcEntities = new NativeArray<Entity>(dynBuff, Allocator.Temp);
                Debug.Assert(srcEntities[0] == prefabRoot);

                RenderMeshArray renderMeshArray = default;
                MaterialMeshInfo defaultMaterialMeshInfo = default;
                RenderFilterSettings defaultRenderFilterSettings = default;

                bool defaultsInitialized = false;

                ScatterLODMeshMaterialIndices meshMaterialMatIndices = new ScatterLODMeshMaterialIndices();
                ScatterLODDistances lodDistances = new ScatterLODDistances();

                meshMaterialMatIndices.MeshMatIndices.Fill(0);
                lodDistances.Distances.Fill(float.PositiveInfinity);

                //gather relevant data from lodgroups 
                for (int i = 1; i < srcEntities.Length; ++i)
                {
                    var ent = srcEntities[i];

                    if (GetRelevantComponents(ent, entMngr, out var meshLODComp, out var meshLODGroupComp))
                    {
                        if (GetRenderData(ent, entMngr, out var matMeshInfo, out var renderFilterSettings, out var meshArray))
                        {
                            if (!defaultsInitialized)
                            {
                                defaultsInitialized = true;
                                renderMeshArray = meshArray;
                                defaultMaterialMeshInfo = matMeshInfo;
                                defaultRenderFilterSettings = renderFilterSettings;
                                lodDistances.Distances[0] = meshLODGroupComp.LODDistances0.x;
                                lodDistances.Distances[1] = meshLODGroupComp.LODDistances0.y;
                                lodDistances.Distances[2] = meshLODGroupComp.LODDistances0.z;
                                lodDistances.Distances[3] = meshLODGroupComp.LODDistances0.w;
                                lodDistances.Distances[4] = meshLODGroupComp.LODDistances1.x;
                                lodDistances.Distances[5] = meshLODGroupComp.LODDistances1.y;
                                lodDistances.Distances[6] = meshLODGroupComp.LODDistances1.z;
                                lodDistances.Distances[7] = meshLODGroupComp.LODDistances1.w;
                            }
                            else
                            {
                                Assert.AreEqual(renderMeshArray.GetHash128(), meshArray.GetHash128());
                            }

                            AssignMeshIndexToRelevantLODs(meshLODGroupComp, (uint)meshLODComp.LODMask, matMeshInfo.Mesh, matMeshInfo.Material, ref meshMaterialMatIndices);
                            hasGraphicsRelevantComponent = true;
                            needsMeshMatComponent = false;
                        }
                    }
                }

                //find last lod that has nonzero mat & mesh indices
                {
                    int lastValidIndex = 0;
                    for (int i = 0; i < 8; ++i)
                    {
                        if (meshMaterialMatIndices.GetMaterialIndex(i) == 0 || meshMaterialMatIndices.GetMeshIndex(i) == 0) break;
                        lastValidIndex = i;
                    }

                    lodDistances.LastLODWithValidEntry = lastValidIndex;
                }

                if (hasGraphicsRelevantComponent)
                {
                    //destroy all other entities except the root
                    {
                        for (int i = 1; i < srcEntities.Length; ++i)
                        {
                            var ent = srcEntities[i];
                            if (ent != Entity.Null)
                            {
                                entMngr.DestroyEntity(ent);
                            }
                        }
                    

                        entMngr.RemoveComponent<LinkedEntityGroup>(prefabRoot);
                        StripLODComponents(prefabRoot, entMngr);
                    }
                

                    //add required components 
                    {
                        RenderMeshDescription rendMeshDesc = default;
                        rendMeshDesc.FilterSettings = defaultRenderFilterSettings;
                        RenderMeshUtility.AddComponents(prefabRoot, entMngr, rendMeshDesc, renderMeshArray, defaultMaterialMeshInfo);

                        entMngr.AddComponentData(prefabRoot, meshMaterialMatIndices);
                        entMngr.AddComponentData(prefabRoot, lodDistances);
                    }
                    srcEntities.Dispose();
                }
            }

            if (needsMeshMatComponent && prefabRoot != Entity.Null)
            {
                if (entMngr.HasComponent<LinkedEntityGroup>(prefabRoot))
                {
                    var dynBuff = entMngr.GetBuffer<LinkedEntityGroup>(prefabRoot, isReadOnly: false).Reinterpret<Entity>().AsNativeArray();
                    var srcEntities = new NativeArray<Entity>(dynBuff, Allocator.Temp);

                    
                    for (int i = 0; i < srcEntities.Length; ++i)
                    {
                        if (GetRenderData(srcEntities[i], entMngr, out var matMeshInfo, out var renderFilterSettings, out var meshArray))
                        {
                            ScatterMeshMaterialIndices meshMaterialIndices;
                            meshMaterialIndices.meshIndex = (short)matMeshInfo.Mesh;
                            meshMaterialIndices.matIndex = (short)matMeshInfo.Material;
                            entMngr.AddComponentData(srcEntities[i], meshMaterialIndices);
                        }
                    }

                    srcEntities.Dispose();
                }
                else
                {
                    if (GetRenderData(prefabRoot, entMngr, out var matMeshInfo, out var renderFilterSettings, out var meshArray))
                    {
                        ScatterMeshMaterialIndices meshMaterialIndices;
                        meshMaterialIndices.meshIndex = (short)matMeshInfo.Mesh;
                        meshMaterialIndices.matIndex = (short)matMeshInfo.Material;
                        entMngr.AddComponentData(prefabRoot, meshMaterialIndices);
                    }
                }
            }

            return prefabRoot;
        }

        static bool GetRelevantComponents(Entity ent, EntityManager entMngr, out MeshLODComponent lodComponent, out MeshLODGroupComponent lodGroupComponent)
        {
            if (entMngr.HasComponent<MeshLODComponent>(ent))
            {
                lodComponent = entMngr.GetComponentData<MeshLODComponent>(ent);
                lodGroupComponent = entMngr.GetComponentData<MeshLODGroupComponent>(lodComponent.Group);
                return true;
            }
            else
            {
                lodComponent = default;
                lodGroupComponent = default;
                return false;
            }
        }

        static bool GetRenderData(Entity ent, EntityManager entMngr, out MaterialMeshInfo matMeshInfo, out RenderFilterSettings renderFilterSettings, out RenderMeshArray renderMeshArray)
        {
            if (entMngr.HasComponent<MaterialMeshInfo>(ent))
            {
                matMeshInfo = entMngr.GetComponentData<MaterialMeshInfo>(ent);
                renderMeshArray = entMngr.GetSharedComponentManaged<RenderMeshArray>(ent);
                renderFilterSettings = entMngr.GetSharedComponentManaged<RenderFilterSettings>(ent);
                return true;
            }

            matMeshInfo = default;
            renderMeshArray = default;
            renderFilterSettings = default;
            return false;
        }

        static void StripLODComponents(Entity ent, EntityManager entMngr)
        {
            ComponentTypeSet componentsToRemove = new(new ComponentType[]
            {
                typeof(MeshLODComponent), typeof(MeshLODGroupComponent), s_ReflectionUtility.GetType((int)TypesToReflect.LODRange), s_ReflectionUtility.GetType((int)TypesToReflect.RootLODRange),
                s_ReflectionUtility.GetType((int)TypesToReflect.LODWorldReferencePoint), s_ReflectionUtility.GetType((int)TypesToReflect.LODGroupWorldReferencePoint), s_ReflectionUtility.GetType((int)TypesToReflect.RootLODWorldReferencePoint),
                typeof(ScatteredInstanceChildren)
            });

            entMngr.RemoveComponent(ent, componentsToRemove);
        }

        static void AssignMeshIndexToRelevantLODs(MeshLODGroupComponent lodGroup, uint lodMask, int meshIndex, int materialIndex, ref ScatterLODMeshMaterialIndices materialIndices)
        {
            if ((lodMask & 0x01) == 0x01)
            {
                materialIndices.SetMeshIndex(0, meshIndex);
                materialIndices.SetMaterialIndex(0, materialIndex);
            }

            if ((lodMask & 0x02) == 0x02)
            {
                materialIndices.SetMeshIndex(1, meshIndex);
                materialIndices.SetMaterialIndex(1, materialIndex);
            }

            if ((lodMask & 0x04) == 0x04)
            {
                materialIndices.SetMeshIndex(2, meshIndex);
                materialIndices.SetMaterialIndex(2, materialIndex);
            }

            if ((lodMask & 0x08) == 0x08)
            {
                materialIndices.SetMeshIndex(3, meshIndex);
                materialIndices.SetMaterialIndex(3, materialIndex);
            }

            if ((lodMask & 0x10) == 0x10)
            {
                materialIndices.SetMeshIndex(4, meshIndex);
                materialIndices.SetMaterialIndex(4, materialIndex);
            }

            if ((lodMask & 0x20) == 0x20)
            {
                materialIndices.SetMeshIndex(5, meshIndex);
                materialIndices.SetMaterialIndex(5, materialIndex);
            }

            if ((lodMask & 0x40) == 0x40)
            {
                materialIndices.SetMeshIndex(6, meshIndex);
                materialIndices.SetMaterialIndex(6, materialIndex);
            }

            if ((lodMask & 0x80) == 0x80)
            {
                materialIndices.SetMeshIndex(7, meshIndex);
                materialIndices.SetMaterialIndex(7, materialIndex);
            }
        }
    }
}