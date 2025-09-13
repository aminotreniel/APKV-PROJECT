using UnityEngine;

namespace TimeGhost
{
    public class SetScenarioOnEnable : MonoBehaviour
    {
        [SerializeField] int scenarioIndex;

        void OnEnable()
        {
            if (TryGetComponent(out LightingScenarioManager mgr))
            {
                mgr.SetScenario(scenarioIndex);
            }
        }
    }
}
