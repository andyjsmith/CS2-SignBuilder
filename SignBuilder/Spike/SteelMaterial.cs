using System;
using Colossal.IO.AssetDatabase;
using UnityEngine;

namespace SignBuilder.Spike
{
    /// <summary>
    /// Builds a tileable galvanized-steel SurfaceAsset from procedural textures, using a vanilla
    /// surface's material template so the game's batch renderer recognizes the shader.
    /// </summary>
    public static class SteelMaterial
    {
        public static Texture2D BaseColor(int size = 512)
        {
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, mipChain: true) { name = "SignBuilder.SteelBase", wrapMode = TextureWrapMode.Repeat };
            var pixels = new Color32[size * size];
            for (var y = 0; y < size; y++)
            for (var x = 0; x < size; x++)
            {
                // Tileable mottling: periodic noise at a few scales.
                var u = x / (float)size;
                var v = y / (float)size;
                var mottle = Periodic(u, v, 4) * 0.6f + Periodic(u, v, 11) * 0.3f + Periodic(u, v, 29) * 0.1f;
                var g = 0.70f + (mottle - 0.5f) * 0.14f;
                pixels[y * size + x] = new Color(g, g * 1.01f, g * 1.03f, 1f);
            }
            tex.SetPixels32(pixels);
            tex.Apply(updateMipmaps: true);
            return tex;
        }

        /// <summary>HDRP mask map: R metallic, G occlusion, B detail mask, A smoothness.</summary>
        public static Texture2D Mask(int size = 256)
        {
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, mipChain: true, linear: true) { name = "SignBuilder.SteelMask", wrapMode = TextureWrapMode.Repeat };
            var pixels = new Color32[size * size];
            for (var y = 0; y < size; y++)
            for (var x = 0; x < size; x++)
            {
                var smooth = 0.42f + (Periodic(x / (float)size, y / (float)size, 7) - 0.5f) * 0.15f;
                pixels[y * size + x] = new Color(0.45f, 1f, 0f, smooth);
            }
            tex.SetPixels32(pixels);
            tex.Apply(updateMipmaps: true);
            return tex;
        }

        /// <summary>Creates, saves and returns the surface. <paramref name="template"/> supplies the shader/template.</summary>
        public static SurfaceAsset Create(ILocalAssetDatabase database, string folder, SurfaceAsset template, Action<string> log)
        {
            template.LoadProperties(useVT: false);
            var templateMaterial = template.GetTemplateMaterial();
            log($"template={templateMaterial?.name} shader={templateMaterial?.shader?.name}");
            if (templateMaterial == null) throw new InvalidOperationException("Donor surface has no template material");

            var material = new Material(templateMaterial) { name = "SignBuilder_Steel" };
            // The template carries textures of its own (blue noise arrays, VT placeholders) that are not
            // CPU-readable; SurfaceAsset tries to save every texture, so drop them (run 7 failure).
            var dropped = new System.Collections.Generic.List<string>();
            var shader = material.shader;
            for (var i = 0; i < shader.GetPropertyCount(); i++)
            {
                if (shader.GetPropertyType(i) != UnityEngine.Rendering.ShaderPropertyType.Texture) continue;
                var prop = shader.GetPropertyName(i);
                var tex = material.GetTexture(prop);
                if (tex != null && !tex.isReadable)
                {
                    material.SetTexture(prop, null);
                    dropped.Add($"{prop}({tex.GetType().Name})");
                }
            }
            log($"dropped unreadable textures: {string.Join(", ", dropped)}");
            material.SetTexture("_BaseColorMap", BaseColor());
            if (material.HasProperty("_MaskMap")) material.SetTexture("_MaskMap", Mask());
            if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", Color.white);

            var path = AssetDataPath.Create(folder, $"SignBuilder_Steel_{DateTime.Now:yyyyMMddHHmmss}");
            var surface = database.AddAsset<SurfaceAsset, Material>(path, material);
            var saved = surface.Save(mipBias: 0, force: true, saveTextures: true, vt: false);
            log($"saved={saved} id={surface.id} textures={string.Join(",", surface.textures.Keys)}");
            return surface;
        }

        private static float Periodic(float u, float v, int period)
        {
            // Sample Perlin noise on a torus-like domain so the texture tiles seamlessly.
            var a = u * Mathf.PI * 2;
            var b = v * Mathf.PI * 2;
            return Mathf.PerlinNoise(Mathf.Cos(a) * period * 0.3f + Mathf.Sin(b) * period * 0.3f + 100f, Mathf.Sin(a) * period * 0.3f + Mathf.Cos(b) * period * 0.3f + 100f);
        }
    }
}
