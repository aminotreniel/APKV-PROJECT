using System;
using UnityEngine;

namespace TimeGhost
{
    [ExecuteAlways]
    public class QualityLevels : MonoBehaviour
    {
        [Serializable]
        public enum Level
        {
            Low,
            Medium,
            High
        }

        public GameObject QualityLowRootGO;
        public GameObject QualityMediumRootGO;
        public GameObject QualityHighRootGO;
        
        public Level QualityLevel;
        
        private Level m_CurrentQualityLevel;

        private void OnEnable()
        {
            SetQualityLevel(QualityLevel);
        }

        private void Update()
        {
            if (Application.isPlaying)
            {
                if (Input.GetKeyUp(KeyCode.Alpha1))
                {
                    QualityLevel = Level.Low;
                }
                if (Input.GetKeyUp(KeyCode.Alpha2))
                {
                    QualityLevel = Level.Medium;
                }
                if (Input.GetKeyUp(KeyCode.Alpha3))
                {
                    QualityLevel = Level.High;
                }
            }

            if (m_CurrentQualityLevel != QualityLevel)
            {
                SetQualityLevel(QualityLevel);
            }
        }

        private void SetQualityLevel(Level lvl)
        {
            m_CurrentQualityLevel = lvl;
            QualityLowRootGO.SetActive(lvl == Level.Low);
            QualityMediumRootGO.SetActive(lvl == Level.Medium);
            QualityHighRootGO.SetActive(lvl == Level.High);
        }
        
    }
}
