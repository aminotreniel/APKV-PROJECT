using UnityEngine;

namespace TimeGhost
{
    public class SceneControlSwitcher : MonoBehaviour
    {
        public GameObject[] SetupsToSwitch;


        void Start()
        {
            ActivateSetup(0);
        }


        void Update()
        {
            for (int i = (int)KeyCode.F1; i != (int)KeyCode.F9; ++i)
            {
                if (Input.GetKeyUp((KeyCode)i))
                {
                    ActivateSetup(i - (int)KeyCode.F1);
                }
            }
        }

        bool IsValidIndex(int index)
        {
            return !(SetupsToSwitch == null || SetupsToSwitch.Length <= index || SetupsToSwitch[index] == null);

        }

        void ActivateSetup(int setupIndex)
        {
            if(!IsValidIndex(setupIndex)) return;

            for (int i = 0; i < SetupsToSwitch.Length; ++i)
            {
                SetupsToSwitch[i].SetActive(i == setupIndex);
            }

        }
    }
}