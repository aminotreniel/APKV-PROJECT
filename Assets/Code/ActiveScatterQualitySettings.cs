using System;

namespace TimeGhost
{
    [Serializable]
    public struct ScatterQualitySettings
    {
        public float ScatterDistance;
        public float ScatterFadeDistance;
        public float TileImpostorLOD0VisibilityDistance;
        public float TileImpostorLOD1VisibilityDistance;
        public float ScreenSizeVisibilityAlways;
        public float ScreenSizeVisibilityNever;
        public float ShadowCullSize;
        public int LODBias;
        public float LODMultiplier;
        public int MaxInstancesToInstantiatePerBatch;
    }
    public class ActiveScatterQualitySettings
    {
        static ActiveScatterQualitySettings s_Instance = null;

        private static ScatterQualitySettings s_DefaultSettings = new ScatterQualitySettings()
        {
            ScatterDistance = 100.0f,
            ScatterFadeDistance = 20.0f,
            TileImpostorLOD0VisibilityDistance = 900.0f,
            TileImpostorLOD1VisibilityDistance = 1500.0f,
            ScreenSizeVisibilityAlways = 0.005f,
            ScreenSizeVisibilityNever = 0.002f,
            ShadowCullSize = 0.001f,
            LODBias = 0,
            LODMultiplier = 1.0f,
            MaxInstancesToInstantiatePerBatch = 30000
        };

        public static ActiveScatterQualitySettings Inst
        {
            get
            {
                if (s_Instance == null)
                {
                    s_Instance = new ActiveScatterQualitySettings();
                    s_Instance.Settings = s_DefaultSettings;
                }

                return s_Instance;
            }
        }
        
        public ScatterQualitySettings Settings
        {
            get;
            set;
        }
    }
}