using System;
using System.Collections.Generic;
using Colossal.IO.AssetDatabase;
using UnityEngine;
using UnityEngine.Rendering;

namespace SignBuilder.Assemblies
{
    /// <summary>
    /// Creates SurfaceAssets (game materials) from our own textures, using a vanilla surface's material
    /// template so the batch renderer recognises the shader. Proven with the steel material (Phase 1).
    /// </summary>
    public static class SurfaceFactory
    {
        public static SurfaceAsset Create(ILocalAssetDatabase database, string folder, string name, SurfaceAsset template,
            Texture2D baseColor, Texture2D mask, Action<Material> configure = null)
        {
            template.LoadProperties(useVT: false);
            var templateMaterial = template.GetTemplateMaterial() ?? throw new InvalidOperationException("Donor surface has no template material");
            var material = new Material(templateMaterial) { name = name };

            // Templates carry textures that are not CPU-readable (snow maps, blue noise); SurfaceAsset
            // saves every texture it references, so drop them.
            var shader = material.shader;
            for (var i = 0; i < shader.GetPropertyCount(); i++)
            {
                if (shader.GetPropertyType(i) != ShaderPropertyType.Texture) continue;
                var prop = shader.GetPropertyName(i);
                var tex = material.GetTexture(prop);
                if (tex != null && !tex.isReadable) material.SetTexture(prop, null);
            }
            material.SetTexture("_BaseColorMap", Savable(baseColor));
            if (mask != null && material.HasProperty("_MaskMap")) material.SetTexture("_MaskMap", Savable(mask));
            if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", Color.white);
            configure?.Invoke(material);

            var path = AssetDataPath.Create(folder, $"{name}_{DateTime.Now:yyyyMMddHHmmssfff}");
            var surface = database.AddAsset<SurfaceAsset, Material>(path, material);
            surface.Save(mipBias: 0, force: true, saveTextures: true, vt: false);
            return surface;
        }

        /// <summary>A sign face: the baked texture, matte non-metallic, transparent corners cut out.</summary>
        public static SurfaceAsset SignFace(ILocalAssetDatabase database, string folder, SurfaceAsset template, Texture2D face, string key)
        {
            face = Savable(face);
            return Create(database, folder, "SignBuilder_Face_" + key.Substring(0, Math.Min(12, key.Length)), template, face, FaceMask(), m =>
            {
                if (m.HasProperty("_AlphaCutoffEnable")) m.SetFloat("_AlphaCutoffEnable", 1f);
                if (m.HasProperty("_AlphaCutoff")) m.SetFloat("_AlphaCutoff", 0.5f);
                // A faint self-illumination stands in for retroreflective sheeting at night.
                if (m.HasProperty("_EmissiveColorMap")) m.SetTexture("_EmissiveColorMap", face);
                if (m.HasProperty("_EmissiveColor")) m.SetColor("_EmissiveColor", Color.white * 0.3f);
            });
        }

        /// <summary>
        /// TextureAsset stores the texture's GraphicsFormat and recreates it on load, so the format must be one
        /// the GPU can sample. ARGB32 (what Texture2D.LoadImage produces) is an internal format that saves
        /// fine but fails on load ("'87' is not supported for Sample usage"); copy such textures to RGBA32.
        /// </summary>
        public static Texture2D Savable(Texture2D tex)
        {
            if (tex == null || tex.format == TextureFormat.RGBA32) return tex;
            var copy = new Texture2D(tex.width, tex.height, TextureFormat.RGBA32, tex.mipmapCount > 1, !tex.isDataSRGB)
            {
                name = tex.name, wrapMode = tex.wrapMode, filterMode = tex.filterMode,
            };
            copy.SetPixels32(tex.GetPixels32());
            copy.Apply(updateMipmaps: true);
            return copy;
        }

        private static Texture2D s_FaceMask;

        /// <summary>HDRP mask: R metallic 0, G occlusion 1, B detail 0, A smoothness 0.35.</summary>
        private static Texture2D FaceMask()
        {
            if (s_FaceMask != null) return s_FaceMask;
            s_FaceMask = new Texture2D(4, 4, TextureFormat.RGBA32, mipChain: false, linear: true) { name = "SignBuilder.FaceMask" };
            var pixels = new Color32[16];
            for (var i = 0; i < pixels.Length; i++) pixels[i] = new Color(0f, 1f, 0f, 0.35f);
            s_FaceMask.SetPixels32(pixels);
            s_FaceMask.Apply();
            return s_FaceMask;
        }
    }
}
