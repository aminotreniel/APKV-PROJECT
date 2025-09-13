using UnityEngine;

#if !UNITY_EDITOR
    using System.Collections;
    using UnityEngine.SceneManagement;
#else
    using UnityEditor;
    using UnityEngine.SceneManagement;
    using UnityEditor.SceneManagement;
#endif

namespace TimeGhost
{
    public class Loader : MonoBehaviour
    {
#if !UNITY_EDITOR
        IEnumerator Start()
        {
            Application.backgroundLoadingPriority = ThreadPriority.High;

            var sceneCount = SceneManager.sceneCountInBuildSettings;
            Debug.Log($"Preparing to load {sceneCount - 1} scenes.");

            var asyncOperations = new AsyncOperation[sceneCount];
            for (var sceneBuildIndex = sceneCount - 1; sceneBuildIndex >= 1; --sceneBuildIndex)
            {
                var scenePath = SceneUtility.GetScenePathByBuildIndex(sceneBuildIndex);

                Debug.Log($"Starting to load scene '{scenePath}'.");
                var asyncOp = SceneManager.LoadSceneAsync(sceneBuildIndex, LoadSceneMode.Additive)!;
                asyncOp.allowSceneActivation = false;
                asyncOperations[sceneBuildIndex] = asyncOp;

                while (asyncOp.progress < 0.9f)
                {
                    yield return null;
                }
            }

            for (var sceneBuildIndex = sceneCount - 1; sceneBuildIndex >= 1; --sceneBuildIndex)
            {
                var scenePath = SceneUtility.GetScenePathByBuildIndex(sceneBuildIndex);
                Debug.Log($"Activating scene '{scenePath}'.");
                
                var asyncOp = asyncOperations[sceneBuildIndex];
                asyncOp.allowSceneActivation = true;
                
                while (!asyncOp.isDone)
                {
                    yield return null;
                }

                var loadedScene = SceneManager.GetSceneByBuildIndex(sceneBuildIndex);
                Debug.Log($"Activated scene '{loadedScene.name}' ({scenePath}).");

                if (sceneBuildIndex == 1)
                {
                    Debug.Log($"Setting scene '{loadedScene.name}' active.");
                    SceneManager.SetActiveScene(loadedScene);
                }
            }

            yield return null;

            Application.backgroundLoadingPriority = ThreadPriority.Normal;
        }
#else

        [MenuItem("Time Ghost/Load Demo Scenes")]
        static void LoadScenes()
        {
            if (!Application.isBatchMode)
            {
                EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo();
            }

            var sceneSetups = new SceneSetup[SceneManager.sceneCountInBuildSettings];
            
            for (var i = 0; i < sceneSetups.Length; ++i)
            {
                sceneSetups[i] = new()
                {
                    path = SceneUtility.GetScenePathByBuildIndex(i),
                    isActive = i == 1,
                    isLoaded = true,
                    isSubScene = false,
                };
            }
            
            EditorSceneManager.RestoreSceneManagerSetup(sceneSetups);
        }
#endif
    }
}
