using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;
using SignBuilder.Core;
using SignBuilder.Geometry;
using SignBuilder.Assemblies;
using SignBuilder.Tools;

namespace SignBuilder.Rendering
{
    /// <summary>
    /// Draws baked sign faces as thin panels with the game's default lit shader: the face on the front,
    /// plain aluminum on the back and edges. Real signs are retroreflective; at night the face gets an
    /// emissive copy of itself (<see cref="NightGlow"/>) so it stays readable.
    /// </summary>
    public sealed class SignPanelRenderer
    {
        public const string kShaderName = "BH/SG_DefaultShader";
        private static readonly int s_EmissiveColor = Shader.PropertyToID("_EmissiveColor");

        private readonly Dictionary<(float, float), Mesh> m_Meshes = new();
        private readonly Dictionary<(Texture, float), Material> m_FaceMaterials = new();
        private readonly List<(Mesh mesh, Material face, Matrix4x4 matrix)> m_Panels = new();
        private Material m_BackMaterial;
        private float m_NightGlow;

        public int Count => m_Panels.Count;

        /// <summary>Emissive strength applied to every face that has no fixed glow of its own.</summary>
        public float NightGlow
        {
            get => m_NightGlow;
            set
            {
                if (Mathf.Approximately(value, m_NightGlow)) return;
                m_NightGlow = value;
                foreach (var kv in m_FaceMaterials)
                {
                    if (kv.Key.Item2 < 0) kv.Value.SetColor(s_EmissiveColor, Color.white * value);
                }
            }
        }

        /// <summary>Adds a panel. <paramref name="fixedGlow"/> &gt;= 0 pins its emissive strength (used for comparisons).</summary>
        public void Add(BakedFace face, Vector3 position, Quaternion rotation, float fixedGlow = -1f)
        {
            if (face?.Texture == null) return;
            var size = (math.round(face.WidthMeters * 100f) / 100f, math.round(face.HeightMeters * 100f) / 100f);
            if (!m_Meshes.TryGetValue(size, out var mesh))
            {
                mesh = GantryFactory.Panel(size.Item1, size.Item2);
                m_Meshes[size] = mesh;
            }
            var key = (face.Texture as Texture, fixedGlow);
            if (!m_FaceMaterials.TryGetValue(key, out var material))
            {
                material = CreateMaterial("SignBuilder.Face." + face.Key, face.Texture, Color.white, 0.35f);
                material.SetFloat("_AlphaCutoffEnable", 1f);
                material.SetFloat("_AlphaCutoff", 0.5f);
                material.SetTexture("_EmissiveColorMap", face.Texture);
                material.SetColor(s_EmissiveColor, Color.white * (fixedGlow >= 0 ? fixedGlow : m_NightGlow));
                HDMaterial.ValidateMaterial(material);
                m_FaceMaterials[key] = material;
            }
            m_Panels.Add((mesh, material, Matrix4x4.TRS(position, rotation, Vector3.one)));
        }

        public void Draw()
        {
            m_BackMaterial ??= CreateMaterial("SignBuilder.PanelBack", Texture2D.whiteTexture, new Color(0.62f, 0.63f, 0.64f), 0.45f);
            foreach (var (mesh, face, matrix) in m_Panels)
            {
                Graphics.DrawMesh(mesh, matrix, face, 0, null, 0, null, ShadowCastingMode.On, true, null, LightProbeUsage.BlendProbes);
                Graphics.DrawMesh(mesh, matrix, m_BackMaterial, 0, null, 1, null, ShadowCastingMode.On, true, null, LightProbeUsage.BlendProbes);
            }
        }

        /// <summary>A sign-face material for immediate drawing (placement preview).</summary>
        public static Material CreateFaceMaterial(Texture face, float glow)
        {
            var material = CreateMaterial("SignBuilder.Face", face, Color.white, 0.35f);
            material.SetFloat("_AlphaCutoffEnable", 1f);
            material.SetFloat("_AlphaCutoff", 0.5f);
            material.SetTexture("_EmissiveColorMap", face);
            material.SetColor(s_EmissiveColor, Color.white * glow);
            HDMaterial.ValidateMaterial(material);
            return material;
        }

        public static Material CreateMaterial(string name, Texture baseMap, Color tint, float smoothness)
        {
            var material = new Material(Shader.Find(kShaderName)) { name = name, enableInstancing = true };
            material.SetTexture("_BaseColorMap", baseMap);
            material.SetColor("_BaseColor", tint);
            material.SetFloat("_Metallic", 0f);
            material.SetFloat("_Smoothness", smoothness);
            if (material.HasProperty("colossal_DecalLayerMask")) material.SetFloat("colossal_DecalLayerMask", math.asfloat(8));
            HDMaterial.ValidateMaterial(material);
            return material;
        }
    }
}
