using UnityEditor;
using UnityEngine;

namespace AI003.EditorTools
{
    /// <summary>
    /// One-off helper: opt the editor into MCP for Unity's auto-start using the
    /// stdio bridge, which makes Unity listen on 127.0.0.1:6400 for the Python
    /// MCP server (the DSH client launches that server).
    /// </summary>
    public static class EnableUnityMcp
    {
        public static void Run()
        {
            EditorPrefs.SetBool("MCPForUnity.AutoStartOnLoad", true);
            EditorPrefs.SetBool("MCPForUnity.UseHttpTransport", false);
            EditorPrefs.SetBool("MCPForUnity.DebugLogs", true);
            Debug.Log("[AI003] Unity MCP auto-start enabled (stdio bridge on 6400)");

            if (Application.isBatchMode)
            {
                EditorApplication.Exit(0);
            }
        }
    }
}
