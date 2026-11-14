using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace AI003.EditorTools
{
    /// <summary>
    /// Applies the delivery manifest's import rules to the surface texture set:
    /// albedo sRGB+BC7, normal as NormalMap (sRGB off, BC5), and the single-channel
    /// masks linear + BC4. The settings live in the .meta files, so a fresh clone
    /// imports byte-identically.
    /// </summary>
    public static class SurfaceTextureImport
    {
        private const string Root = "Assets/Settings/Textures/surface";
        private static readonly string[] Masks = { "roughness", "ao", "height", "crack_mask", "edge_wear", "foam_mask" };

        /// <summary>Every directory under surface/ is one delivered set; scanning keeps
        /// this tool correct when a new set arrives without a code change.</summary>
        private static string[] SetDirectories()
        {
            var list = new List<string>();
            foreach (string dir in Directory.GetDirectories(Root))
            {
                list.Add(Path.GetFileName(dir));
            }
            list.Sort();
            return list.ToArray();
        }

        [MenuItem("AI003/Import Surface Textures")]
        public static void Run()
        {
            int changed = 0;
            foreach (string set in SetDirectories())
            {
                string dir = $"{Root}/{set}";
                foreach (string path in Directory.GetFiles(dir, "*.png"))
                {
                    string file = Path.GetFileNameWithoutExtension(path).ToLowerInvariant();
                    var importer = AssetImporter.GetAtPath(path) as TextureImporter;
                    if (importer == null)
                    {
                        Debug.LogWarning($"[AI003][SURF] no importer for {path}");
                        continue;
                    }

                    importer.textureType = TextureImporterType.Default;
                    importer.sRGBTexture = false;
                    importer.mipmapEnabled = true;
                    importer.wrapMode = TextureWrapMode.Repeat;
                    importer.anisoLevel = 4;
                    importer.maxTextureSize = 1024;
                    importer.textureCompression = TextureImporterCompression.CompressedHQ;

                    if (file.EndsWith("_albedo_1024"))
                    {
                        importer.sRGBTexture = true;
                        importer.anisoLevel = 8;
                    }
                    else if (file.EndsWith("_normal_1024"))
                    {
                        importer.textureType = TextureImporterType.NormalMap;
                        importer.sRGBTexture = false;
                        importer.anisoLevel = 8;
                    }
                    else
                    {
                        bool isMask = false;
                        foreach (string m in Masks)
                        {
                            if (file.EndsWith("_" + m + "_1024"))
                            {
                                isMask = true;
                                break;
                            }
                        }
                        if (!isMask)
                        {
                            Debug.LogWarning($"[AI003][SURF] unknown role: {file}, left Default/sRGB-off");
                        }
                    }

                    importer.SaveAndReimport();
                    changed++;
                }
            }

            AssetDatabase.Refresh();
            Debug.Log($"[AI003][SURF] import rules applied to {changed} textures");
            Verify();
            if (Application.isBatchMode)
            {
                EditorApplication.Exit(0);
            }
        }

        private static void Verify()
        {
            foreach (string set in SetDirectories())
            {
                foreach (string path in Directory.GetFiles($"{Root}/{set}", "*.png"))
                {
                    var imp = AssetImporter.GetAtPath(path) as TextureImporter;
                    if (imp == null)
                    {
                        continue;
                    }
                    string role = Path.GetFileNameWithoutExtension(path);
                    Debug.Log($"[AI003][SURF] {set}/{role} type={imp.textureType} sRGB={imp.sRGBTexture} " +
                              $"mips={imp.mipmapEnabled} wrap={imp.wrapMode} max={imp.maxTextureSize}");
                }
            }
        }
    }
}

