using Unity.Entities;

namespace TimeGhost
{
    public class TileImpostorPrefabBaker
    {
        public class ScatteringPrefabBaker : Baker<TileImpostorPrefab>
        {
            
            public override void Bake(TileImpostorPrefab authoring)
            {
                var entity = GetEntity(TransformUsageFlags.Renderable);
                var lodReferences = AddBuffer<TileImpostorSystem.TileImpostorPrefabLODEntry>(entity);
                
                for (int i = 0; i < authoring.LODs.Length; ++i)
                {
                    var lod = GetEntity(authoring.LODs[i], TransformUsageFlags.Renderable);
                    lodReferences.Add(new TileImpostorSystem.TileImpostorPrefabLODEntry()
                    {
                        Value = lod
                    });
                }
                
                AddComponent(entity, new TileImpostorSystem.TileImpostorInfo()
                {
                    numberOfInstancesBaked = authoring.numberOfInstancesBaked
                });
            }
        }
    }
}