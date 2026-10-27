using System;
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
    /// Values: editorOpen, templates, editingDesign, toolStatus.
    /// Triggers: setEditorOpen(bool), startPlacement(json { design, support }), cancelTool().
    /// </summary>
    public partial class SignBuilderUISystem : UISystemBase
    {
        public const string Group = "SignBuilder";

        private ValueBinding<bool> m_EditorOpen;
        private ValueBinding<string> m_Templates;
        private ValueBinding<string> m_EditingDesign;
        private ValueBinding<string> m_ToolStatus;
        private SignPlacementToolSystem m_Tool;

        public bool EditorOpen => m_EditorOpen.value;

        protected override void OnCreate()
        {
            base.OnCreate();
            m_Tool = World.GetOrCreateSystemManaged<SignPlacementToolSystem>();
            AddBinding(m_EditorOpen = new ValueBinding<bool>(Group, "editorOpen", false));
            AddBinding(m_Templates = new ValueBinding<string>(Group, "templates", "[]"));
            AddBinding(m_EditingDesign = new ValueBinding<string>(Group, "editingDesign", ""));
            AddBinding(m_ToolStatus = new ValueBinding<string>(Group, "toolStatus", ""));
            AddBinding(new TriggerBinding<bool>(Group, "setEditorOpen", SetEditorOpen));
            AddBinding(new TriggerBinding<string>(Group, "startPlacement", StartPlacement));
            AddBinding(new TriggerBinding(Group, "cancelTool", () => m_Tool.BeginPick()));
        }

        protected override void OnGameLoadingComplete(Colossal.Serialization.Entities.Purpose purpose, Game.GameMode mode)
        {
            base.OnGameLoadingComplete(purpose, mode);
            // The editor preview loads renderer.js/css and fonts from the mod folder through this host.
            GameManager.instance.userInterface.view.uiSystem.AddHostLocation("signbuilder", SignBuilderRuntime.ResourcesDir, shouldWatch: false);
            m_Templates.Update(LoadTemplates());
        }

        public void SetEditorOpen(bool open)
        {
            m_EditorOpen.Update(open);
            if (open)
            {
                if (m_Templates.value == "[]") m_Templates.Update(LoadTemplates());
                m_Tool.BeginPick();
            }
            else
            {
                m_Tool.Deactivate();
                SetStatus("");
            }
        }

        public void StartPlacement(string payload)
        {
            try
            {
                var obj = JObject.Parse(payload);
                // { designs: [...] } (several signs on one structure) or the older { design }.
                var designs = obj["designs"] is JArray list ? list.OfType<JObject>().ToList() : new System.Collections.Generic.List<JObject> { (JObject)obj["design"] };
                var support = (string)obj["support"] ?? "truss";
                if (!AssemblyBuilder.Supports.Contains(support)) support = "truss";
                m_Tool.BeginPlace(designs, support, (bool?)obj["reverse"] ?? false);
            }
            catch (Exception e)
            {
                SetStatus("Could not read the sign design: " + e.Message);
            }
        }

        /// <summary>Opens the editor on a placed assembly's design (re-editing).</summary>
        public void OpenForEdit(AssemblyRecord record)
        {
            m_EditorOpen.Update(true);
            // A fresh object each time so the React effect fires even for the same assembly.
            m_EditingDesign.Update(new JObject { ["designs"] = new JArray(record.Designs), ["support"] = record.Support, ["reverse"] = record.Reverse, ["nonce"] = Guid.NewGuid().ToString("N") }.ToString(Formatting.None));
        }

        public void SetStatus(string status) => m_ToolStatus.Update(status ?? "");

        public string Status => m_ToolStatus.value;

        private static string LoadTemplates()
        {
            var dir = Path.Combine(SignBuilderRuntime.ResourcesDir, "Templates");
            if (!Directory.Exists(dir)) return "[]";
            var list = new JArray();
            foreach (var file in Directory.GetFiles(dir, "*.json").OrderBy(f => f))
            {
                try
                {
                    var design = JObject.Parse(File.ReadAllText(file));
                    list.Add(new JObject
                    {
                        ["id"] = Path.GetFileNameWithoutExtension(file),
                        ["name"] = (string)design["name"] ?? Path.GetFileNameWithoutExtension(file),
                        ["support"] = design["support"],
                        ["design"] = design,
                    });
                }
                catch (Exception e)
                {
                    Mod.log.Warn($"Skipping template {file}: {e.Message}");
                }
            }
            return list.ToString(Formatting.None);
        }
    }
}
