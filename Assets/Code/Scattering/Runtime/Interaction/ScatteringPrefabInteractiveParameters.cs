using System.Collections;
using System.Collections.Generic;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;

namespace TimeGhost
{
    public class ScatteringPrefabInteractiveParameters : MonoBehaviour
    {
        public float physicsStiffnessMin = 1f;
        public float physicsStiffnessMax = 20f;
        public float physicsDampingMin = 0.5f;
        public float physicsDampingMax = 1f;
        
        public float physicsBreakingAngleInDegreesMin = 30f;
        public float physicsBreakingAngleInDegreesMax = 80f;
        public float physicsRecoveryAngleInDegreesMin = 10f;
        public float physicsRecoveryAngleInDegreesMax = 70f;
        

        public void SetFrom(ScatteringPrefabInteractiveParameters other)
        {
            physicsStiffnessMin = other.physicsStiffnessMin;
            physicsStiffnessMax = other.physicsStiffnessMax;
            physicsDampingMin = other.physicsDampingMin;
            physicsDampingMax = other.physicsDampingMax;
            
            physicsBreakingAngleInDegreesMin = other.physicsBreakingAngleInDegreesMin;
            physicsBreakingAngleInDegreesMax = other.physicsBreakingAngleInDegreesMax;
            physicsRecoveryAngleInDegreesMin = other.physicsRecoveryAngleInDegreesMin;
            physicsRecoveryAngleInDegreesMax = other.physicsRecoveryAngleInDegreesMax;
        }
    }
}