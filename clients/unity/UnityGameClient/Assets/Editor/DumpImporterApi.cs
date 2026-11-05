using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace AI003.EditorTools
{
    /// <summary>Dumps importer-override APIs so the bake pipeline can call the right one.</summary>
    public static class DumpImporterApi
    {
        public static void Run()
        {
            const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;

            foreach (MethodInfo method in typeof(AssetDatabase).GetMethods(Flags)
                         .Where(m => m.Name.Contains("Importer")))
            {
                Debug.Log($"[AI003][API] AssetDatabase.{method.Name}({string.Join(", ", method.GetParameters().Select(p => p.ParameterType.Name + " " + p.Name))})");
            }

            foreach (MethodInfo method in typeof(AssetImporter).GetMethods(Flags)
                         .Where(m => m.Name.Contains("Override") || m.Name.Contains("Importer")))
            {
                Debug.Log($"[AI003][API] AssetImporter.{method.Name}({string.Join(", ", method.GetParameters().Select(p => p.ParameterType.Name + " " + p.Name))})");
            }

            System.Type importerType = System.Type.GetType("Unity.Scenes.Editor.SubSceneImporter, Unity.Scenes.Editor");
            Debug.Log($"[AI003][API] SubSceneImporter type={importerType}");
            if (importerType != null)
            {
                foreach (MethodInfo method in importerType.GetMethods(Flags).Where(m => m.Name.Contains("Override")))
                {
                    Debug.Log($"[AI003][API] {importerType.Name}.{method.Name}({string.Join(", ", method.GetParameters().Select(p => p.ParameterType.Name))})");
                }
            }

            EditorApplication.Exit(0);
        }
    }
}
