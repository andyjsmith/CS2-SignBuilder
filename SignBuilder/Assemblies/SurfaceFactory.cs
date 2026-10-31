using System;
using System.Collections.Generic;
using Colossal.IO.AssetDatabase;
using UnityEngine;
using UnityEngine.Rendering;

namespace SignBuilder.Assemblies
{
    /// <summary>
    /// Creates SurfaceAssets (game materials) from our own textures, using a vanilla surface's material
    /// template so the batch renderer recognizes the shader. Proven with the steel material (Phase 1).
    /// </summary>
    public static class SurfaceFactory
    {
        public static SurfaceAsset Create(ILocalAssetDatabase database, string folder, string name, SurfaceAsset template,
            Texture2D baseColor, Texture2D mask, Action<Material> configure = null)
        {
            template.LoadProperties(useVT: false);
            var templateMaterial = template.GetTemplateMaterial() ?? throw new InvalidOperationException("Donor surface has no template material");
            var material = new Material(templateMaterial) { name = name };

            // SurfaceAsset saves a copy of every texture its material references, and the template's own (snow maps,
            // blue noise) would be saved again with each of our materials: drop them all, we set what we use.
            var shader = material.shader;
            for (var i = 0; i < shader.GetPropertyCount(); i++)
            {
                if (shader.GetPropertyType(i) != ShaderPropertyType.Texture) continue;
                material.SetTexture(shader.GetPropertyName(i), null);
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

        /// <summary>How bright LED lettering glows: its light's intensity (RuntimePrefabs.AddGlow).</summary>
        public const float kLedGlow = 3f;

        /// <summary>The share of the face's own color that glows around LED lettering (a faintly lit board).</summary>
        private const float kLedBoardGlow = 0.1f;

        /// <summary>
        /// A sign face: the baked texture, matte non-metallic, transparent corners cut out. <paramref name="lit"/> (LED
        /// lettering, the face's size) is its glow map; the game only draws it for submeshes its mesh lists as lights.
        /// </summary>
        public static SurfaceAsset SignFace(ILocalAssetDatabase database, string folder, SurfaceAsset template, Texture2D face, string key, Texture2D lit = null)
        {
            face = Savable(face);
            // Saved at half the face's size: enough to keep the dots sharp.
            var led = lit != null && lit.width == face.width && lit.height == face.height;
            var emissive = led ? Downscale(Emissive(face, lit, kLedBoardGlow), 2) : null;
            return Create(database, folder, "SignBuilder_Face_" + key.Substring(0, Math.Min(12, key.Length)), template, face, FaceMask(), m =>
            {
                if (m.HasProperty("_AlphaCutoffEnable")) m.SetFloat("_AlphaCutoffEnable", 1f);
                if (m.HasProperty("_AlphaCutoff")) m.SetFloat("_AlphaCutoff", 0.5f);
                if (m.HasProperty("_EmissiveColor")) m.SetColor("_EmissiveColor", led ? Color.white : Color.black);
                if (led && m.HasProperty("_EmissiveColorMap")) m.SetTexture("_EmissiveColorMap", emissive);
            });
        }

        /// <summary>A copy <paramref name="factor"/> times smaller each way (averaged), with mipmaps.</summary>
        private static Texture2D Downscale(Texture2D source, int factor)
        {
            var w = Math.Max(1, source.width / factor);
            var h = Math.Max(1, source.height / factor);
            var src = source.GetPixels32();
            var dst = new Color32[w * h];
            for (var y = 0; y < h; y++)
            {
                for (var x = 0; x < w; x++)
                {
                    int r = 0, g = 0, b = 0, a = 0, n = 0;
                    for (var dy = 0; dy < factor; dy++)
                    {
                        var sy = y * factor + dy;
                        if (sy >= source.height) break;
                        for (var dx = 0; dx < factor; dx++)
                        {
                            var sx = x * factor + dx;
                            if (sx >= source.width) break;
                            var c = src[sy * source.width + sx];
                            r += c.r; g += c.g; b += c.b; a += c.a; n++;
                        }
                    }
                    dst[y * w + x] = new Color32((byte)(r / n), (byte)(g / n), (byte)(b / n), (byte)(a / n));
                }
            }
            var tex = new Texture2D(w, h, TextureFormat.RGBA32, mipChain: true) { name = "SignBuilder.Glow", wrapMode = TextureWrapMode.Clamp };
            tex.SetPixels32(dst);
            tex.Apply(updateMipmaps: true);
            return tex;
        }

        /// <summary>The face dimmed by <paramref name="faceShare"/>, with the LED lettering at full brightness over it.</summary>
        private static Texture2D Emissive(Texture2D face, Texture2D lit, float faceShare)
        {
            var a = face.GetPixels32();
            var b = lit.GetPixels32();
            var o = new Color32[a.Length];
            for (var i = 0; i < a.Length; i++)
            {
                var k = b[i].a / 255f;
                byte Mix(byte f, byte l) => (byte)Math.Min(255f, Math.Max(f * faceShare, l * k));
                o[i] = new Color32(Mix(a[i].r, b[i].r), Mix(a[i].g, b[i].g), Mix(a[i].b, b[i].b), a[i].a);
            }
            var tex = new Texture2D(face.width, face.height, TextureFormat.RGBA32, mipChain: true) { name = "SignBuilder.Glow", wrapMode = TextureWrapMode.Clamp };
            tex.SetPixels32(o);
            tex.Apply(updateMipmaps: true);
            return tex;
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
