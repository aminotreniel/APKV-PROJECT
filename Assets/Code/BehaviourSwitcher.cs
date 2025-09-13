using UnityEngine;

namespace TimeGhost
{
    public class BehaviourSwitcher : MonoBehaviour
    {
        enum Mode { EnableTarget, DisableTarget }

        [SerializeField] Mode mode = Mode.EnableTarget;
        [SerializeField] Behaviour target;

        void OnEnable()
        {
            if (target)
            {
                target.enabled = mode == Mode.EnableTarget;
            }
        }

        void OnDisable()
        {
            if (target)
            {
                target.enabled = mode != Mode.EnableTarget;
            }
        }
    }
}
