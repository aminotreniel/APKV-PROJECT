using Unity.Entities;

namespace TimeGhost
{

    public class IgnoreScatteringMaxDistanceCullingBaker : Baker<IgnoreScatteringMaxDistanceCulling>
    {

        public override void Bake(IgnoreScatteringMaxDistanceCulling authoring)
        {
            AddComponent<IgnoreMaxDistanceCullingTag>(GetEntity(TransformUsageFlags.None));
        }



    }
}