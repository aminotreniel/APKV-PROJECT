using UnityEngine.Playables;

namespace TimeGhost
{
    public class LightingScenarioPlayableBehaviour : PlayableBehaviour
    {
        public string scenarioName;

#if NO_BLEND_ONLY
        public override void ProcessFrame(Playable playable, FrameData info, object playerData)
        {
            base.ProcessFrame(playable, info, playerData);

            if (info.weight > 0f && !string.IsNullOrEmpty(scenarioName))
            {
                var mgr = (LightingScenarioManager)playerData;
                mgr.SetScenario(scenarioName);
            }
        }
#endif
    }
}
