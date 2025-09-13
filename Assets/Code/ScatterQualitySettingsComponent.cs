using System;
using UnityEngine;

namespace TimeGhost
{
    [ExecuteAlways]
    public class ScatterQualitySettingsComponent : MonoBehaviour
    {
        public ScatterQualitySettings settings;

        private void OnEnable()
        {
            ActiveScatterQualitySettings.Inst.Settings = settings;
        }
    }
}