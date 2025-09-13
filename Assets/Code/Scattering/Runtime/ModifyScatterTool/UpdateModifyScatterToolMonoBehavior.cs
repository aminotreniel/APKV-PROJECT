using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace TimeGhost
{
    [ExecuteAlways]
    public class UpdateModifyScatterToolMonoBehavior : MonoBehaviour
    {
        private static UpdateModifyScatterToolMonoBehavior s_Instance;
        public static UpdateModifyScatterToolMonoBehavior GetInstance() 
        {
            if (s_Instance == null)
            {
                var ghosts = FindObjectsByType<UpdateModifyScatterToolMonoBehavior>(FindObjectsSortMode.None);
                foreach (var ghost in ghosts)
                {
                    CoreUtils.Destroy(ghost);
                }
                s_Instance = new GameObject("ModifyScatterPointCloudUtility").AddComponent<UpdateModifyScatterToolMonoBehavior>();
                var o = s_Instance.gameObject;
                o.hideFlags = HideFlags.HideAndDontSave;
            }
            return s_Instance;
        }
        
        public delegate void Tick();
        public event Tick OnUpdate;
        public event Tick OnLateUpdate;

        private void Update()
        {
            OnUpdate?.Invoke();
        }

        private void LateUpdate()
        {
            OnLateUpdate?.Invoke();
        }
    }
}