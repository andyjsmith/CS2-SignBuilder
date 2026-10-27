using System;
using System.Collections;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Colossal.UI;
using Game.SceneFlow;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace HighwaySigns.Rendering
{
    /// <summary>A sign face baked to a texture, with its physical size.</summary>
    public sealed class BakedFace
    {
        public Texture2D Texture;
        public float WidthMeters;
        public float HeightMeters;
        public float PixelsPerInch;
        public bool FromCache;
        public string Key;
    }

    /// <summary>
    /// Bakes sign designs (JSON, see Resources/Renderer/renderer.js) into textures with the game's
    /// Gameface UI engine, off-screen.
    ///
    /// The renderer page is loaded once; each design is pushed with View.ExecuteScript. The sign is
    /// drawn top-left on a transparent page and its pixel bounding box gives the physical size, so
    /// nothing has to be read back from JavaScript. Results are cached as PNGs keyed by a hash of the
    /// design, the resolution and the renderer sources, so a design is only ever rendered once.
    ///
    /// Drawing recipe (proven in the Phase 0 spikes): the view gets a dummy, disabled camera so the
    /// UISystem never draws or blits it; it is sized and enabled explicitly, and we issue
    /// BackendBeginFrame / DrawView / BackendEndFrame ourselves from endFrameRendering.
    /// </summary>
    public sealed class SignFaceBaker : IDisposable
    {
        private const string kHost = "highwaysigns";
        private const int kViewWidth = 2048;
        private const int kViewHeight = 1024;
        private const float kMetersPerInch = 0.0254f;

        private readonly string m_ResourcesDir;
        private readonly string m_CacheDir;
        private readonly string m_RendererVersion;
        private UISystem m_UISystem;
        private UIView m_View;
        private Camera m_DummyCamera;
        private MethodInfo m_SendRenderEvent;
        private object m_BeginFrame;
        private object m_EndFrame;
        private bool m_Loaded;
        private bool m_Drawing;

        public SignFaceBaker(string resourcesDir, string cacheDir)
        {
            m_ResourcesDir = resourcesDir;
            m_CacheDir = cacheDir;
            Directory.CreateDirectory(m_CacheDir);
            // Any change to the renderer sources invalidates every cached face.
            var rendererDir = Path.Combine(resourcesDir, "Renderer");
            m_RendererVersion = Hash(File.ReadAllText(Path.Combine(rendererDir, "renderer.js")) + File.ReadAllText(Path.Combine(rendererDir, "renderer.css")));
        }

        public string LastError { get; private set; }

        /// <summary>How the last face was produced: "script" (ExecuteScript) or "reload" (URL navigation).</summary>
        public string LastMode { get; private set; }

        /// <summary>Whether ExecuteScript re-rendering works in this game build; null until first tried.</summary>
        public bool? ScriptMode { get; private set; }

        /// <summary>Diagnostics for the ExecuteScript path (it did not repaint in the first Phase 1 runs).</summary>
        public string ScriptProbe { get; private set; }

        private bool m_UseB;

        /// <summary>Bakes <paramref name="design"/> and yields until <paramref name="onDone"/> has been called (with null on failure).</summary>
        public IEnumerator Bake(JObject design, float pixelsPerInch, Action<BakedFace> onDone)
        {
            var json = design.ToString(Formatting.None);
            var key = Hash($"{m_RendererVersion}|{pixelsPerInch}|{json}");
            var cached = TryLoadCached(key);
            if (cached != null)
            {
                onDone(cached);
                yield break;
            }

            if (m_View == null) yield return Initialise();
            if (!m_Loaded)
            {
                onDone(null);
                yield break;
            }

            // Re-render smaller if the sign does not fit the view.
            for (var attempt = 0; attempt < 4; attempt++)
            {
                var ppiText = pixelsPerInch.ToString(System.Globalization.CultureInfo.InvariantCulture);
                byte[] pixels = null;
                int width = 0, height = 0;
                var bounds = default(RectInt);
                var found = false;

                // Fast path: push the design into the already-loaded page.
                if (ScriptMode != false)
                {
                    ScriptProbe = $"readyForBindings={m_View.View.IsReadyForBindings()}";
                    m_View.View.ExecuteScript($"window.hsRender({json}, {ppiText});");
                    yield return DrawFrames(4);
                    pixels = Readback(out width, out height);
                    found = pixels != null && FindBounds(pixels, width, height, out bounds);
                    if (ScriptMode == null) ScriptMode = found;
                }

                // Fallback: navigate to the page with the design in the URL hash, alternating between two
                // identical pages so every bake is a full navigation (the path proven in the spikes).
                if (!found)
                {
                    m_Loaded = false;
                    m_UseB = !m_UseB;
                    m_View.url = $"coui://{kHost}/Renderer/{(m_UseB ? "renderer-b.html" : "renderer.html")}#{ppiText}|{Uri.EscapeDataString(json)}";
                    var deadline = Time.realtimeSinceStartup + 8f;
                    while (!m_Loaded && Time.realtimeSinceStartup < deadline) yield return null;
                    yield return new WaitForSecondsRealtime(0.5f);
                    yield return DrawFrames(6);
                    pixels = Readback(out width, out height);
                    found = pixels != null && FindBounds(pixels, width, height, out bounds);
                    LastMode = "reload";
                }
                else
                {
                    LastMode = "script";
                }

                if (pixels == null) { LastError = "GPU readback failed"; break; }
                if (!found) { LastError = "rendered nothing"; break; }
                // The sign is anchored top-left (row height-1 after the flip); touching the right edge
                // or the bottom row means it was clipped.
                if (bounds.xMax >= width || bounds.yMin <= 0)
                {
                    pixelsPerInch *= 0.7f;
                    continue;
                }

                var face = new BakedFace
                {
                    Texture = Crop(pixels, width, bounds),
                    PixelsPerInch = pixelsPerInch,
                    WidthMeters = bounds.width / pixelsPerInch * kMetersPerInch,
                    HeightMeters = bounds.height / pixelsPerInch * kMetersPerInch,
                    Key = key,
                };
                Save(key, face);
                onDone(face);
                yield break;
            }
            onDone(null);
        }

        private IEnumerator Initialise()
        {
            m_UISystem = GameManager.instance.userInterface.view.uiSystem;
            m_UISystem.AddHostLocation(kHost, m_ResourcesDir, shouldWatch: false);

            var go = new GameObject("HighwaySigns.SignFaceBakerCamera");
            Object.DontDestroyOnLoad(go);
            m_DummyCamera = go.AddComponent<Camera>();
            m_DummyCamera.enabled = false;
            m_DummyCamera.cullingMask = 0;

            var renderEvents = Type.GetType("cohtml.RenderEvents, Cohtml.Runtime");
            var eventType = Type.GetType("cohtml.RenderEventType, Cohtml.Runtime");
            m_SendRenderEvent = renderEvents?.GetMethod("SendRenderEvent", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
            if (m_SendRenderEvent == null || eventType == null)
            {
                LastError = "cohtml.RenderEvents.SendRenderEvent not found";
                yield break;
            }
            m_BeginFrame = Enum.Parse(eventType, "BackendBeginFrame");
            m_EndFrame = Enum.Parse(eventType, "BackendEndFrame");

            var settings = UIView.Settings.New;
            settings.acceptsInput = false;
            settings.isTransparent = true;
            settings.pixelPerfect = false;
            settings.liveReload = false;
            settings.enableBackdropFilter = false;
            settings.listener.FinishLoad += _ => m_Loaded = true;
            settings.listener.LoadFailed += (url, error) => LastError = $"{url}: {error}";
            m_View = m_UISystem.CreateView(null, settings, m_DummyCamera);
            // UIView ignores the settings size (it sizes from a camera when pixelPerfect) and starts
            // disabled; a disabled view never loads or draws.
            m_View.Resize(kViewWidth, kViewHeight);
            m_View.enabled = true;
            m_View.url = $"coui://{kHost}/Renderer/renderer.html";
            RenderPipelineManager.endFrameRendering += OnEndFrame;

            var deadline = Time.realtimeSinceStartup + 10f;
            while (!m_Loaded && LastError == null && Time.realtimeSinceStartup < deadline) yield return null;
            if (!m_Loaded) LastError ??= "renderer page did not load";
            // Give @font-face fonts a moment before the first sign.
            yield return new WaitForSecondsRealtime(0.5f);
        }

        private IEnumerator DrawFrames(int settleFrames)
        {
            // Let layout, fonts and <img> shields settle, then draw a few frames.
            for (var i = 0; i < settleFrames; i++) yield return null;
            m_Drawing = true;
            for (var i = 0; i < 4; i++) yield return null;
            yield return new WaitForEndOfFrame();
            m_Drawing = false;
        }

        private void OnEndFrame(ScriptableRenderContext ctx, Camera[] cameras)
        {
            if (!m_Drawing) return;
            m_SendRenderEvent.Invoke(null, new[] { m_BeginFrame, m_UISystem.id, 0u });
            m_View.Draw();
            m_SendRenderEvent.Invoke(null, new[] { m_EndFrame, m_UISystem.id, 0u });
        }

        /// <summary>Reads the view back as RGBA32, flipped so row 0 is the bottom (Unity order).</summary>
        private byte[] Readback(out int width, out int height)
        {
            width = height = 0;
            if (m_View.ViewTexture is not RenderTexture rt) return null;
            var request = AsyncGPUReadback.Request(rt, 0, TextureFormat.RGBA32);
            request.WaitForCompletion();
            if (request.hasError) return null;
            width = rt.width;
            height = rt.height;
            var data = request.GetData<byte>();
            // cohtml renders top row first; Unity textures store the bottom row first.
            var rowBytes = width * 4;
            var flipped = new byte[data.Length];
            for (var y = 0; y < height; y++)
            {
                NativeArray<byte>.Copy(data, y * rowBytes, flipped, (height - 1 - y) * rowBytes, rowBytes);
            }
            return flipped;
        }

        private static bool FindBounds(byte[] pixels, int width, int height, out RectInt bounds)
        {
            int minX = width, minY = height, maxX = -1, maxY = -1;
            for (var y = 0; y < height; y++)
            {
                var row = y * width * 4;
                for (var x = 0; x < width; x++)
                {
                    if (pixels[row + x * 4 + 3] == 0) continue;
                    if (x < minX) minX = x;
                    if (x > maxX) maxX = x;
                    if (y < minY) minY = y;
                    if (y > maxY) maxY = y;
                }
            }
            bounds = maxX < 0 ? default : new RectInt(minX, minY, maxX - minX + 1, maxY - minY + 1);
            return maxX >= 0;
        }

        private static Texture2D Crop(byte[] pixels, int width, RectInt b)
        {
            var cropped = new byte[b.width * b.height * 4];
            for (var y = 0; y < b.height; y++)
            {
                Buffer.BlockCopy(pixels, ((b.y + y) * width + b.x) * 4, cropped, y * b.width * 4, b.width * 4);
            }
            var tex = new Texture2D(b.width, b.height, TextureFormat.RGBA32, mipChain: true) { name = "HighwaySigns.Face", wrapMode = TextureWrapMode.Clamp };
            tex.SetPixelData(cropped, 0);
            tex.Apply(updateMipmaps: true);
            return tex;
        }

        // ------------------------------------------------------------------ cache

        private BakedFace TryLoadCached(string key)
        {
            var png = Path.Combine(m_CacheDir, key + ".png");
            var meta = Path.Combine(m_CacheDir, key + ".json");
            if (!File.Exists(png) || !File.Exists(meta)) return null;
            try
            {
                var info = JObject.Parse(File.ReadAllText(meta));
                var tex = new Texture2D(2, 2, TextureFormat.RGBA32, mipChain: true) { name = "HighwaySigns.Face", wrapMode = TextureWrapMode.Clamp };
                if (!tex.LoadImage(File.ReadAllBytes(png))) return null;
                return new BakedFace
                {
                    Texture = tex,
                    WidthMeters = (float)info["widthMeters"],
                    HeightMeters = (float)info["heightMeters"],
                    PixelsPerInch = (float)info["ppi"],
                    FromCache = true,
                    Key = key,
                };
            }
            catch (Exception)
            {
                return null;
            }
        }

        private void Save(string key, BakedFace face)
        {
            try
            {
                File.WriteAllBytes(Path.Combine(m_CacheDir, key + ".png"), face.Texture.EncodeToPNG());
                File.WriteAllText(Path.Combine(m_CacheDir, key + ".json"), new JObject
                {
                    ["widthMeters"] = face.WidthMeters,
                    ["heightMeters"] = face.HeightMeters,
                    ["ppi"] = face.PixelsPerInch,
                }.ToString());
            }
            catch (Exception e)
            {
                Mod.log.Warn($"Could not cache sign face {key}: {e.Message}");
            }
        }

        private static string Hash(string text)
        {
            using var sha = SHA1.Create();
            var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(text));
            var sb = new StringBuilder(bytes.Length * 2);
            foreach (var b in bytes) sb.Append(b.ToString("x2"));
            return sb.ToString();
        }

        public void Dispose()
        {
            RenderPipelineManager.endFrameRendering -= OnEndFrame;
            if (m_View != null) m_UISystem.DestroyView(m_View);
            if (m_DummyCamera != null) Object.Destroy(m_DummyCamera.gameObject);
        }
    }
}
