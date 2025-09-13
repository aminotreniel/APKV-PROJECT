using UnityEngine;
using UnityEngine.Playables;
using UnityEngine.Timeline;

namespace TimeGhost
{
    [TrackClipType(typeof(LightingScenarioPlayableAsset))]
    [TrackBindingType(typeof(LightingScenarioManager))]
    public class LightingScenarioTrackAsset : TrackAsset
    {
        public override Playable CreateTrackMixer(PlayableGraph graph, GameObject go, int inputCount)
        {
            return ScriptPlayable<LightingScenarioMixerBehaviour>.Create(graph, inputCount);
        }

        public override void GatherProperties(PlayableDirector director, IPropertyCollector driver)
        {
            base.GatherProperties(director, driver);
            
            var binding = director.GetGenericBinding(this) as IPropertyPreview;
            binding?.GatherProperties(director, driver);
        }
    }
}
