using UnityEngine;
using UnityEngine.Playables;

namespace TimeGhost
{
    public class LightingScenarioMixerBehaviour : PlayableBehaviour
    {
        class Cleanup
        {
            public LightingScenarioManager Manager;
            public float Scenario;
        }

        Cleanup cleanup;

        public override void OnPlayableDestroy(Playable playable)
        {
            if (cleanup != null)
            {
                if (cleanup.Manager != null)
                {
                    //Debug.Log($"Cleanup apply {cleanup.Scenario}");
                    cleanup.Manager.SetIsTimelineDriven(false);
                    cleanup.Manager.SetScenarioBlend(cleanup.Scenario, true);
                    cleanup.Manager.ScenarioBlend = cleanup.Scenario;
                }
                
                cleanup = null;
            }
            
            base.OnPlayableDestroy(playable);
        }

        public override void ProcessFrame(Playable playable, FrameData info, object playerData)
        {
            base.ProcessFrame(playable, info, playerData);

            if (playerData == null)
                return;

            int activeCount = 0;
            string name0 = null, name1 = null;
            float blend = 0f;
            
            var inputCount = playable.GetInputCount();
            for (var i = 0; i < inputCount; ++i)
            {
                var inputWeight = playable.GetInputWeight(i);
                if (inputWeight <= 0f)
                {
                    continue;
                }

                ScriptPlayable<LightingScenarioPlayableBehaviour> inputPlayable = (ScriptPlayable<LightingScenarioPlayableBehaviour>)playable.GetInput(i);
                LightingScenarioPlayableBehaviour input = inputPlayable.GetBehaviour();

                if (activeCount == 0)
                {
                    name0 = input.scenarioName;
                }
                else if (activeCount == 1)
                {
                    name1 = input.scenarioName;
                    blend = inputWeight;
                }
                else
                {
                    throw new UnityException($"Unable to blend more than two scenarios.");
                }
                
                ++activeCount;
            }

            var mgr = (LightingScenarioManager)playerData;
            if (activeCount > 0)
            {
                if (Application.isPlaying && cleanup == null)
                {
                    //Debug.Log($"Cleanup create {mgr.ScenarioBlend}");
                    cleanup = new Cleanup { Manager = mgr, Scenario = mgr.ScenarioBlend };
                }
                
                mgr.SetIsTimelineDriven(true);
                mgr.SetScenarioBlend(name0, name1, blend);
                //Debug.Log($"Drive {name0} {name1} {blend} ({mgr.ScenarioBlend} / {mgr.LastScenarioBlend})");
            }
            else if(!Application.isPlaying || cleanup != null)
            {
                //Debug.Log($"UnDrive");
                mgr.SetIsTimelineDriven(false);
            }
        }
    }
}