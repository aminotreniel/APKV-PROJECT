using UnityEngine;
using UnityEditor;

public class AlignSceneCamera : ScriptableObject
{
    [MenuItem("Custom/Align Scene View with Main Camera _F12")] // here '_F12' is a hotkey.
    private static void Align()
    {
        if (Camera.main != null)
        {
            SceneView.lastActiveSceneView.AlignViewToObject(Camera.main.transform);
        }
    }
}
