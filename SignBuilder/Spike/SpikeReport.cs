using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

namespace SignBuilder.Spike
{
    /// <summary>
    /// Collects spike findings as ordered key/value pairs, mirrors them to the mod log and
    /// rewrites ModsData/SignBuilder/spike/results.json after every test, so a crash late in
    /// the run never loses earlier results.
    /// </summary>
    internal static class SpikeReport
    {
        private static readonly List<KeyValuePair<string, string>> s_Entries = new();

        public static string OutputDir => Path.Combine(Application.persistentDataPath, "ModsData", "SignBuilder", "spike");

        public static void Set(string key, object value)
        {
            var text = value?.ToString() ?? "null";
            s_Entries.Add(new KeyValuePair<string, string>(key, text));
            Mod.log.Info($"[spike] {key} = {text}");
        }

        public static void Write()
        {
            try
            {
                Directory.CreateDirectory(OutputDir);
                var sb = new StringBuilder("{\n");
                for (var i = 0; i < s_Entries.Count; i++)
                {
                    sb.Append("  \"").Append(Escape(s_Entries[i].Key)).Append("\": \"").Append(Escape(s_Entries[i].Value)).Append('"');
                    sb.Append(i < s_Entries.Count - 1 ? ",\n" : "\n");
                }
                sb.Append("}\n");
                File.WriteAllText(Path.Combine(OutputDir, "results.json"), sb.ToString());
            }
            catch (Exception e)
            {
                Mod.log.Error(e, "Failed to write spike report");
            }
        }

        public static void SavePng(Texture2D texture, string fileName)
        {
            Directory.CreateDirectory(OutputDir);
            File.WriteAllBytes(Path.Combine(OutputDir, fileName), texture.EncodeToPNG());
        }

        public static void Screenshot(string fileName)
        {
            Directory.CreateDirectory(OutputDir);
            ScreenCapture.CaptureScreenshot(Path.Combine(OutputDir, fileName));
            Set($"screenshot.{fileName}", $"t={Time.realtimeSinceStartup:F1}");
        }

        private static string Escape(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "");
    }
}
