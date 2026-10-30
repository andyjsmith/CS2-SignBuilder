using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Colossal.UI.Binding;
using Game.SceneFlow;
using Game.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SignBuilder.Assemblies;
using SignBuilder.Core;
using SignBuilder.Tools;

namespace SignBuilder.Systems
{
    /// <summary>
    /// Bridges the React editor (UI/src/mods) and the C# side. Binding group = mod id "SignBuilder".
    /// Values: editorOpen, templates, editingDesign, toolStatus, toolMode (off|posts|pickRoad|arrange),
    /// placeInfo, arrangeInfo, replacing.
    /// Triggers: setEditorOpen, startPosts, startGantry, adjust, arrange, savePosts, cancelTool,
    /// saveTemplate, renameTemplate, deleteTemplate, openTemplatesFolder.
    /// </summary>
    public partial class SignBuilderUISystem : UISystemBase
    {
        public const string Group = "SignBuilder";

        private ValueBinding<bool> m_EditorOpen;
        private ValueBinding<string> m_Templates;
        private ValueBinding<string> m_Themes;
        private ValueBinding<string> m_EditingDesign;
        private ValueBinding<string> m_ToolStatus;
        private ValueBinding<string> m_ToolMode;
        private ValueBinding<string> m_PlaceInfo;
        private ValueBinding<string> m_ArrangeInfo;
        private ValueBinding<bool> m_Replacing;
        private SignPlacementToolSystem m_Tool;

        public bool EditorOpen => m_EditorOpen.value;

        /// <summary>Player templates: one JSON file per template, easy to share.</summary>
        public static string UserTemplatesDir => Path.Combine(SignBuilderRuntime.DataDir, "Templates");
        private static string HiddenFile => Path.Combine(SignBuilderRuntime.DataDir, "hidden-templates.json");

        protected override void OnCreate()
        {
            base.OnCreate();
            m_Tool = World.GetOrCreateSystemManaged<SignPlacementToolSystem>();
            AddBinding(m_EditorOpen = new ValueBinding<bool>(Group, "editorOpen", false));
            AddBinding(m_Templates = new ValueBinding<string>(Group, "templates", "[]"));
            AddBinding(m_Themes = new ValueBinding<string>(Group, "themes", "[]"));
            AddBinding(m_EditingDesign = new ValueBinding<string>(Group, "editingDesign", ""));
            AddBinding(m_ToolStatus = new ValueBinding<string>(Group, "toolStatus", ""));
            AddBinding(m_ToolMode = new ValueBinding<string>(Group, "toolMode", "off"));
            AddBinding(m_PlaceInfo = new ValueBinding<string>(Group, "placeInfo", "{}"));
            AddBinding(m_ArrangeInfo = new ValueBinding<string>(Group, "arrangeInfo", ""));
            AddBinding(m_Replacing = new ValueBinding<bool>(Group, "replacing", false));
            AddBinding(new TriggerBinding<bool>(Group, "setEditorOpen", SetEditorOpen));
            AddBinding(new TriggerBinding<string>(Group, "startPosts", json => Guard(() =>
            {
                var o = JObject.Parse(json);
                var surface = (string)o["mount"] == "surface";
                m_Tool.BeginPosts((JObject)o["design"], new LayoutOptions { Type = surface ? "surface" : "ground", Free = surface || ((bool?)o["free"] ?? false) });
            })));
            AddBinding(new TriggerBinding<string>(Group, "startGantry", json => Guard(() =>
            {
                var o = JObject.Parse(json);
                m_Tool.BeginGantry(o["designs"] is JArray list ? list.OfType<JObject>() : Enumerable.Empty<JObject>());
            })));
            AddBinding(new TriggerBinding<string>(Group, "adjust", a => m_Tool.Adjust(a)));
            AddBinding(new TriggerBinding<string>(Group, "arrange", json => m_Tool.Arrange(json)));
            AddBinding(new TriggerBinding<string>(Group, "savePosts", json => Guard(() =>
            {
                var o = JObject.Parse(json);
                m_Tool.SavePosts((JObject)o["design"], (bool?)o["lights"] ?? false);
            })));
            AddBinding(new TriggerBinding(Group, "cancelTool", () => { m_Tool.Deactivate(); SetStatus(""); }));
            AddBinding(new TriggerBinding<string>(Group, "saveTemplate", json => Guard(() => SaveTemplate(JObject.Parse(json)))));
            AddBinding(new TriggerBinding<string, string>(Group, "renameTemplate", (id, name) => Guard(() => RenameTemplate(id, name))));
            AddBinding(new TriggerBinding<string>(Group, "deleteTemplate", id => Guard(() => DeleteTemplate(id))));
            AddBinding(new TriggerBinding(Group, "openTemplatesFolder", () =>
            {
                Directory.CreateDirectory(UserTemplatesDir);
                UnityEngine.Application.OpenURL("file:///" + UserTemplatesDir.Replace('\\', '/'));
            }));
            // Player themes: one folder per theme (theme.json, fonts, shields, templates), read when the editor opens.
            AddBinding(new TriggerBinding(Group, "openThemesFolder", () => Setting.OpenFolder(Themes.UserThemesDir)));
        }

        private void Guard(Action action, [System.Runtime.CompilerServices.CallerLineNumber] int line = 0)
        {
            Core.SignBuilderRuntime.Mark($"UI action (SignBuilderUISystem.cs line {line})");
            var watch = System.Diagnostics.Stopwatch.StartNew();
            try { action(); }
            catch (Exception e)
            {
                Mod.log.Warn(e, "Sign Builder UI action failed");
                SetStatus("Something went wrong: " + e.Message);
            }
            if (watch.ElapsedMilliseconds > 250) Mod.log.Info($"UI action (SignBuilderUISystem.cs line {line}) took {watch.ElapsedMilliseconds} ms");
        }

        protected override void OnUpdate()
        {
            base.OnUpdate();
            var mode = m_Tool.ModeName;
            if (mode != m_ToolMode.value) m_ToolMode.Update(mode);
            var info = m_Tool.PlacementInfo;
            if (info != m_PlaceInfo.value) m_PlaceInfo.Update(info);
            var arrange = m_Tool.ArrangeInfo();
            if (arrange != m_ArrangeInfo.value) m_ArrangeInfo.Update(arrange);
            if (m_Tool.IsReplacing != m_Replacing.value) m_Replacing.Update(m_Tool.IsReplacing);
        }

        public string ToolMode => m_ToolMode.value;
        public string ArrangeInfoJson => m_ArrangeInfo.value;
        public string TemplatesJson => m_Templates.value;

        protected override void OnGameLoadingComplete(Colossal.Serialization.Entities.Purpose purpose, Game.GameMode mode)
        {
            base.OnGameLoadingComplete(purpose, mode);
            // The editor preview loads renderer.js/css and fonts from the mod folder through this host.
            var ui = GameManager.instance.userInterface.view.uiSystem;
            ui.AddHostLocation("signbuilder", SignBuilderRuntime.ResourcesDir, shouldWatch: false);
            Directory.CreateDirectory(SignBuilderRuntime.DataDir);
            ui.AddHostLocation(Themes.DataHost, SignBuilderRuntime.DataDir, shouldWatch: false);
            foreach (var (host, dir) in Themes.ModHosts) ui.AddHostLocation(host, dir, shouldWatch: false);
            RefreshLibrary();
            if (mode == Game.GameMode.Game) SignBuilderRuntime.Run(SignBuilderRuntime.Baker.Warmup());
        }

        public void SetEditorOpen(bool open)
        {
            m_EditorOpen.Update(open);
            if (open) RefreshLibrary();
            else
            {
                m_Tool.Deactivate();
                SetStatus("");
            }
        }

        /// <summary>Re-reads themes and templates from disk (players may have added files).</summary>
        public void RefreshLibrary()
        {
            Themes.Reload();
            m_Themes.Update(Themes.Json());
            m_Templates.Update(LoadTemplates());
        }

        /// <summary>Opens the editor on a placed structure's signs (kind: "posts" or "gantry").</summary>
        public void OpenForEdit(AssemblyRecord record, string kind, bool lights = false)
        {
            m_EditorOpen.Update(true);
            // A fresh object each time so the React effect fires even for the same structure.
            m_EditingDesign.Update(new JObject
            {
                ["designs"] = new JArray(record.Designs), ["kind"] = kind, ["lights"] = lights, ["nonce"] = Guid.NewGuid().ToString("N"),
            }.ToString(Formatting.None));
        }

        public void SetStatus(string status) => m_ToolStatus.Update(status ?? "");

        public string Status => m_ToolStatus.value;

        // ------------------------------------------------------------------ templates

        private static HashSet<string> Hidden()
        {
            try { return File.Exists(HiddenFile) ? JsonConvert.DeserializeObject<HashSet<string>>(File.ReadAllText(HiddenFile)) ?? new HashSet<string>() : new HashSet<string>(); }
            catch { return new HashSet<string>(); }
        }

        /// <summary>Each theme's templates (minus hidden ones), then the player's own; every design names its theme.</summary>
        private static string LoadTemplates()
        {
            var list = new JArray();
            var hidden = Hidden();
            void AddFrom(string dir, bool builtIn, string theme)
            {
                if (!Directory.Exists(dir)) return;
                foreach (var file in Directory.GetFiles(dir, "*.json").OrderBy(f => f))
                {
                    var id = (builtIn ? $"builtin:{theme}/" : "user:") + Path.GetFileNameWithoutExtension(file);
                    if (hidden.Contains(id)) continue;
                    try
                    {
                        var design = JObject.Parse(File.ReadAllText(file));
                        if (design["theme"] == null) design["theme"] = theme ?? Themes.DefaultId;
                        list.Add(new JObject
                        {
                            ["id"] = id, ["builtIn"] = builtIn,
                            ["name"] = (string)design["name"] ?? Path.GetFileNameWithoutExtension(file),
                            ["theme"] = design["theme"],
                            ["design"] = design,
                        });
                    }
                    catch (Exception e)
                    {
                        Mod.log.Warn($"Skipping template {file}: {e.Message}");
                    }
                }
            }
            foreach (var theme in Themes.All) AddFrom(Path.Combine(theme.Dir, "templates"), true, theme.Id);
            AddFrom(UserTemplatesDir, false, null);
            return list.ToString(Formatting.None);
        }

        private static string Slug(string name)
        {
            var chars = (name ?? "sign").ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray();
            var slug = new string(chars).Trim('-');
            while (slug.Contains("--")) slug = slug.Replace("--", "-");
            return slug.Length == 0 ? "sign" : slug.Length > 60 ? slug.Substring(0, 60) : slug;
        }

        private static string UserPath(string id) => id != null && id.StartsWith("user:") ? Path.Combine(UserTemplatesDir, id.Substring(5) + ".json") : null;

        /// <summary>{ name, design, id? }: overwrites a player template with that id, or creates a new file.</summary>
        public void SaveTemplate(JObject payload)
        {
            Directory.CreateDirectory(UserTemplatesDir);
            var design = (JObject)payload["design"];
            var name = (string)payload["name"] ?? "My sign";
            design["name"] = name;
            var path = UserPath((string)payload["id"]);
            if (path == null)
            {
                var slug = Slug(name);
                path = Path.Combine(UserTemplatesDir, slug + ".json");
                for (var n = 2; File.Exists(path); n++) path = Path.Combine(UserTemplatesDir, $"{slug}-{n}.json");
            }
            File.WriteAllText(path, design.ToString(Formatting.Indented));
            m_Templates.Update(LoadTemplates());
            SetStatus($"Saved template \"{name}\".");
        }

        public void RenameTemplate(string id, string name)
        {
            var path = UserPath(id);
            if (path == null || !File.Exists(path)) return;
            var design = JObject.Parse(File.ReadAllText(path));
            design["name"] = name;
            File.WriteAllText(path, design.ToString(Formatting.Indented));
            m_Templates.Update(LoadTemplates());
        }

        /// <summary>Deletes a player template file; built-in templates are hidden instead.</summary>
        public void DeleteTemplate(string id)
        {
            var path = UserPath(id);
            if (path != null)
            {
                if (File.Exists(path)) File.Delete(path);
            }
            else if (id != null && id.StartsWith("builtin:"))
            {
                var hidden = Hidden();
                hidden.Add(id);
                Directory.CreateDirectory(SignBuilderRuntime.DataDir);
                File.WriteAllText(HiddenFile, JsonConvert.SerializeObject(hidden));
            }
            m_Templates.Update(LoadTemplates());
        }
    }
}
