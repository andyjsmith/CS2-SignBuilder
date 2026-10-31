using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;
using SignBuilder.Core;
using SignBuilder.Geometry;
using SignBuilder.Assemblies;
using SignBuilder.Tools;

namespace SignBuilder.Tests
{
    /// <summary>
    /// Draws baked sign faces as thin panels with the game's default lit shader (proven in runs 2-6).
    /// One material per face texture; every panel is a Graphics.DrawMesh call per frame.
    /// </summary>
    internal sealed class SignPanels
    {
        public const string kShaderName = "BH/SG_DefaultShader";

        private readonly Dictionary<(float, float), Mesh> m_Meshes = new();
        private readonly Dictionary<Texture, Material> m_Materials = new();
        private readonly List<(Mesh mesh, Material material, Matrix4x4 matrix)> m_Panels = new();

        public int Count => m_Panels.Count;

        public void Add(Texture face, float width, float height, Vector3 position, Quaternion rotation)
        {
            if (face == null) return;
            if (!m_Meshes.TryGetValue((width, height), out var mesh))
            {
                mesh = GantryFactory.Panel(width, height);
                m_Meshes[(width, height)] = mesh;
            }
            if (!m_Materials.TryGetValue(face, out var material))
            {
                material = new Material(Shader.Find(kShaderName)) { name = "SignBuilder.Panel." + face.name, enableInstancing = true };
                material.SetTexture("_BaseColorMap", face);
                material.SetColor("_BaseColor", Color.white);
                material.SetFloat("_Metallic", 0f);
                material.SetFloat("_Smoothness", 0.35f);
                material.SetFloat("_AlphaCutoffEnable", 1f);
                material.SetFloat("_AlphaCutoff", 0.5f);
                if (material.HasProperty("colossal_DecalLayerMask")) material.SetFloat("colossal_DecalLayerMask", math.asfloat(8));
                HDMaterial.ValidateMaterial(material);
                m_Materials[face] = material;
            }
            m_Panels.Add((mesh, material, Matrix4x4.TRS(position, rotation, Vector3.one)));
        }

        public void Draw()
        {
            foreach (var (mesh, material, matrix) in m_Panels)
            {
                Graphics.DrawMesh(mesh, matrix, material, 0, null, 0, null, ShadowCastingMode.On, true, null, LightProbeUsage.BlendProbes);
            }
        }
    }
}
