#if UNITY_EDITOR

// Put me inside an Editor/ folder or an editor-only assembly, please.

using System.Reflection;
using UnityEngine;
using UnityEditor;
using UnityEditor.ShortcutManagement;

namespace Code.Editor
{
    public class CustomShortcutActions
    {
        static System.Type s_ClipBoardContextMenuType = typeof(UnityEditor.Editor).Assembly.GetType("UnityEditor.ClipboardContextMenu");
        static MethodInfo s_CopyWorldTransform = s_ClipBoardContextMenuType.GetMethod("CopyTransformWorldPlacementMenu", BindingFlags.NonPublic|BindingFlags.Static);
        static MethodInfo s_PasteWorldTransform = s_ClipBoardContextMenuType.GetMethod("PasteTransformWorldPlacementMenu", BindingFlags.NonPublic|BindingFlags.Static);
            
        //[MenuItem("Tools/Copy World Transform")]
        [Shortcut("copyworldtransform", displayName = "Transform/Copy World Transform")]
        public static void CopyWorldTransform()
        {
            if (Selection.activeGameObject)
            {
                var cmd = new MenuCommand(Selection.activeGameObject.GetComponent<Transform>());
                s_CopyWorldTransform.Invoke(null, new object[] { cmd });
            }
        }
        
        //[MenuItem("Tools/Paste World Transform")]
        [Shortcut("pasteworldtransform", displayName = "Transform/Paste World Transform")]
        public static void PasteWorldTransform()
        {
            if (Selection.activeGameObject)
            {
                var cmd = new MenuCommand(Selection.activeGameObject.GetComponent<Transform>());
                s_PasteWorldTransform.Invoke(null, new object[] { cmd });
            }
        }
    }
}

#endif
