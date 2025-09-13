using UnityEngine;
using UnityEngine.Playables;

namespace TimeGhost
{
    public class LightingScenarioPlayableAsset : PlayableAsset
    {
        public string scenarioName;
        
        public override Playable CreatePlayable (PlayableGraph graph, GameObject owner)
        {
            var playable = ScriptPlayable<LightingScenarioPlayableBehaviour>.Create(graph);
            playable.GetBehaviour().scenarioName = scenarioName;
            return playable;
        }
    }
}
