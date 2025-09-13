using Unity.Entities;
using Unity.Rendering;

namespace TimeGhost
{
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateBefore(typeof(EndSimulationEntityCommandBufferSystem))]
    [WorldSystemFilter(WorldSystemFilterFlags.Default | WorldSystemFilterFlags.Editor)]
    public partial class PruneUploadPoolSystem : SystemBase
    {
        private int maxUploadBufferPoolSize = 150 * 1024 * 1024;
        private int numberOfFramesToPrune = 60;
        private int frameCount = 0;
        protected override void OnCreate()
        {
            frameCount = 0;
        }
        
        protected override void OnDestroy()
        {

        }

        protected override void OnUpdate()
        {
            if (frameCount > numberOfFramesToPrune)
            {
                var gs = World.GetExistingSystemManaged<EntitiesGraphicsSystem>();
                if (gs != null)
                {
                    gs.PruneUploadBufferPool(maxUploadBufferPoolSize);
                }

                frameCount = 0;
            }
            else
            {
                ++frameCount;
            }
        }
    }
}