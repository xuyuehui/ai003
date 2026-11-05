using UnityEditor;
using UnityEngine;

namespace AI003.EditorTools
{
    /// <summary>Clears the model-yaw PlayerPrefs keys (diagnostics).</summary>
    public static class ResetModelYaw
    {
        [MenuItem("AI003/Reset Model Yaw")]
        public static void Run()
        {
            Debug.Log($"[AI003][YAW] before extra={PlayerPrefs.GetFloat("AI003.ModelYawExtra", -1f)} old={PlayerPrefs.GetFloat("AI003.ModelYaw", -1f)}");
            PlayerPrefs.DeleteKey("AI003.ModelYawExtra");
            PlayerPrefs.DeleteKey("AI003.ModelYaw");
            PlayerPrefs.Save();
            Debug.Log($"[AI003][YAW] after extra={PlayerPrefs.GetFloat("AI003.ModelYawExtra", -1f)} old={PlayerPrefs.GetFloat("AI003.ModelYaw", -1f)}");
            EditorApplication.Exit(0);
        }
    }
}
