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
    /// Values: editorOpen, templates, editingDesign, toolStatus, toolMode (off|pick|place).
    /// Triggers: setEditorOpen(bool), startPlacement(json { design, support }), cancelTool().
    /// </summary>
    public partial class SignBuilderUISystem : UISystemBase
    {
        public const string Group = "SignBuilder";

        private ValueBinding<bool> m_EditorOpen;
        private ValueBinding<string> m_Templates;
        private ValueBinding<string> m_EditingDesign;
        private ValueBinding<string> m_ToolStatus;
        private ValueBinding<string> m_ToolMode;
        private ValueBinding<string> m_PlaceInfo;
        private ValueBinding<bool> m_Replacing;
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
            AddBinding(m_ToolMode = new ValueBinding<string>(Group, "toolMode", "off"));
            AddBinding(m_PlaceInfo = new ValueBinding<string>(Group, "placeInfo", "{}"));
            AddBinding(m_Replacing = new ValueBinding<bool>(Group, "replacing", false));
            AddBinding(new TriggerBinding<string>(Group, "adjust", a => m_Tool.Adjust(a)));
            AddBinding(new TriggerBinding<string>(Group, "saveEdit", SaveEdit));
            AddBinding(new TriggerBinding(Group, "forgetEdit", () => m_Tool.ForgetReplacing()));
            AddBinding(new TriggerBinding<bool>(Group, "setEditorOpen", SetEditorOpen));
            AddBinding(new TriggerBinding<string>(Group, "startPlacement", StartPlacement));
            AddBinding(new TriggerBinding(Group, "cancelTool", () => m_Tool.BeginPick()));
        }

        protected override void OnUpdate()
        {
            base.OnUpdate();
            // The editor shrinks to a small bar while placing so the road stays clickable.
            var mode = m_Tool.ModeName;
            if (mode != m_ToolMode.value) m_ToolMode.Update(mode);
            var info = m_Tool.PlacementInfo;
            if (info != m_PlaceInfo.value) m_PlaceInfo.Update(info);
            if (m_Tool.IsReplacing != m_Replacing.value) m_Replacing.Update(m_Tool.IsReplacing);
        }

        public string ToolMode => m_ToolMode.value;

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
                var (designs, support, reverse, free) = ReadPayload(payload);
                m_Tool.BeginPlace(designs, support, reverse, free);
            }
            catch (Exception e)
            {
                SetStatus("Could not read the sign design: " + e.Message);
            }
        }

        /// <summary>{ designs: [...] } (several signs on one structure) or the older { design }, plus support, reverse, free.</summary>
        private static (System.Collections.Generic.List<JObject> designs, string support, bool reverse, bool free) ReadPayload(string payload)
        {
            var obj = JObject.Parse(payload);
            var designs = obj["designs"] is JArray list ? list.OfType<JObject>().ToList() : new System.Collections.Generic.List<JObject> { (JObject)obj["design"] };
            var support = (string)obj["support"] ?? "truss";
            if (!AssemblyBuilder.Supports.Contains(support)) support = "truss";
            return (designs, support, (bool?)obj["reverse"] ?? false, (bool?)obj["free"] ?? false);
        }

        public void SaveEdit(string payload)
        {
            try
            {
                var (designs, support, reverse, _) = ReadPayload(payload);
                m_Tool.SaveEdit(designs, support, reverse);
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
            m_EditingDesign.Update(new JObject
            {
                ["designs"] = new JArray(record.Designs), ["support"] = record.Support, ["reverse"] = record.Reverse,
                ["free"] = record.Context?.Options?.Free ?? false, ["nonce"] = Guid.NewGuid().ToString("N"),
            }.ToString(Formatting.None));
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
