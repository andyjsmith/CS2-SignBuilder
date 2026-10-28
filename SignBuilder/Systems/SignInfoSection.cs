using Colossal.Entities;
using Colossal.UI.Binding;
using Game.Prefabs;
using Game.UI.InGame;
using SignBuilder.Assemblies;
using SignBuilder.Tools;
using Unity.Entities;

namespace SignBuilder.Systems
{
    /// <summary>
    /// A section in the game's selected-object panel for placed Sign Builder structures, with an
    /// "Edit in Sign Builder" button (UI: SignInfoSection.tsx). Clicking a sign with the normal
    /// selection tool is the natural way to find it again, so editing starts from there.
    /// </summary>
    public partial class SignInfoSection : InfoSectionBase
    {
        private SignPlacementToolSystem m_Tool;
        private AssemblyRecord m_Record;

        protected override string group => nameof(SignInfoSection);

        protected override void OnCreate()
        {
            base.OnCreate();
            m_Tool = World.GetOrCreateSystemManaged<SignPlacementToolSystem>();
            m_InfoUISystem.AddMiddleSection(this);
            AddBinding(new TriggerBinding(SignBuilderUISystem.Group, "editSelected", EditSelected));
        }

        private AssemblyRecord RecordOf(Entity entity)
        {
            if (entity == Entity.Null || !EntityManager.TryGetComponent(entity, out PrefabRef prefabRef)) return null;
            return m_PrefabSystem.TryGetPrefab(prefabRef, out PrefabBase prefab) ? AssemblyLibrary.FromPrefabName(prefab.name) : null;
        }

        protected override void OnUpdate()
        {
            visible = RecordOf(selectedEntity) != null;
        }

        protected override void Reset()
        {
            m_Record = null;
        }

        protected override void OnProcess()
        {
            m_Record = RecordOf(selectedEntity);
        }

        public override void OnWriteProperties(IJsonWriter writer)
        {
            writer.PropertyName("support");
            writer.Write(m_Record?.Support ?? "");
            writer.PropertyName("signs");
            writer.Write(m_Record?.Designs?.Count ?? 0);
        }

        internal void EditSelected()
        {
            var entity = selectedEntity;
            if (RecordOf(entity) == null) return;
            m_InfoUISystem.SetSelection(Entity.Null);
            m_Tool.EditPlaced(entity);
        }
    }
}
