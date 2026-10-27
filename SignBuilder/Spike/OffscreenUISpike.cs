using System;
using System.Collections;
using System.Reflection;
using Colossal.UI;
using Game.SceneFlow;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace SignBuilder.Spike
{
    /// <summary>
    /// Renders HTML pages with the game's Gameface (cohtml) engine into off-screen textures.
    ///
    /// Proven recipe (run 6): create the view with a dummy, disabled camera so the UISystem never
    /// draws or blits it, size it explicitly, enable it, then issue BackendBeginFrame / DrawView /
    /// BackendEndFrame ourselves from endFrameRendering and read the result back on the GPU.
    /// </summary>
    internal sealed class OffscreenUISpike : IDisposable
    {
        private const string kHost = "signbuilder";

        private readonly UISystem m_UISystem;
        private readonly UIView m_View;
        private readonly Camera m_DummyCamera;
        private readonly MethodInfo m_SendRenderEvent;
        private readonly object m_BeginFrame;
        private readonly object m_EndFrame;
        private bool m_Drawing;
        private string m_LoadedUrl;
        private string m_FailedUrl;

        public OffscreenUISpike(string resourcesDir)
        {
            m_UISystem = GameManager.instance.userInterface.view.uiSystem;
            m_UISystem.AddHostLocation(kHost, resourcesDir, shouldWatch: false);

            var go = new GameObject("SignBuilder.OffscreenUICamera");
            Object.DontDestroyOnLoad(go);
            m_DummyCamera = go.AddComponent<Camera>();
            m_DummyCamera.enabled = false;
            m_DummyCamera.cullingMask = 0;

            var settings = UIView.Settings.New;
            settings.acceptsInput = false;
            settings.isTransparent = true;
            settings.pixelPerfect = false;
            settings.liveReload = false;
            settings.enableBackdropFilter = false;
            settings.listener.FinishLoad += url => m_LoadedUrl = url;
            settings.listener.LoadFailed += (url, error) => { m_FailedUrl = url; SpikeReport.Set("A.loadFailed", $"{url}: {error}"); };
            m_View = m_UISystem.CreateView(null, settings, m_DummyCamera);
            // Views start disabled; a disabled view is never advanced (so pages never load) and
            // Draw() is a no-op. The game enables its own views right after creating them too.
            m_View.enabled = true;

            var renderEvents = Type.GetType("cohtml.RenderEvents, Cohtml.Runtime");
            var eventType = Type.GetType("cohtml.RenderEventType, Cohtml.Runtime");
            m_SendRenderEvent = renderEvents?.GetMethod("SendRenderEvent", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
            if (eventType != null)
            {
                m_BeginFrame = Enum.Parse(eventType, "BackendBeginFrame");
                m_EndFrame = Enum.Parse(eventType, "BackendEndFrame");
            }
            if (m_SendRenderEvent == null) throw new MissingMethodException("cohtml.RenderEvents.SendRenderEvent not found");
            RenderPipelineManager.endFrameRendering += OnEndFrame;
        }

        /// <summary>Loads <paramref name="page"/> at the given size and yields until the result is in <paramref name="onDone"/> (null on failure).</summary>
        public IEnumerator Render(string page, int width, int height, Action<Texture2D, int> onDone)
        {
            m_LoadedUrl = null;
            m_FailedUrl = null;
            m_View.Resize(width, height);
            var url = $"coui://{kHost}/{page}";
            m_View.url = url;

            var deadline = Time.realtimeSinceStartup + 8f;
            while (m_LoadedUrl != url && m_FailedUrl != url && Time.realtimeSinceStartup < deadline) yield return null;
            if (m_LoadedUrl != url) { onDone(null, 0); yield break; }

            // Fonts and <img> SVGs resolve after FinishLoad; give layout a moment, then draw a few frames.
            yield return new WaitForSecondsRealtime(0.75f);
            m_Drawing = true;
            for (var i = 0; i < 10; i++) yield return null;
            yield return new WaitForEndOfFrame();
            m_Drawing = false;

            var tex = Readback(out var opaque);
            onDone(tex, opaque);
        }

        private void OnEndFrame(ScriptableRenderContext ctx, Camera[] cameras)
        {
            if (!m_Drawing) return;
            m_SendRenderEvent.Invoke(null, new[] { m_BeginFrame, m_UISystem.id, 0u });
            m_View.Draw();
            m_SendRenderEvent.Invoke(null, new[] { m_EndFrame, m_UISystem.id, 0u });
        }

        private Texture2D Readback(out int opaquePixels)
        {
            opaquePixels = 0;
            if (m_View.ViewTexture is not RenderTexture rt) return null;

            var request = AsyncGPUReadback.Request(rt, 0, TextureFormat.RGBA32);
            request.WaitForCompletion();
            if (request.hasError) return null;
            var data = request.GetData<byte>();

            // cohtml renders top row first; Unity textures store the bottom row first. Flip rows.
            var rowBytes = rt.width * 4;
            var flipped = new byte[data.Length];
            for (var y = 0; y < rt.height; y++)
            {
                NativeArray<byte>.Copy(data, y * rowBytes, flipped, (rt.height - 1 - y) * rowBytes, rowBytes);
            }

            var tex = new Texture2D(rt.width, rt.height, TextureFormat.RGBA32, mipChain: true) { name = "SignBuilder.SignFace" };
            tex.SetPixelData(flipped, 0);
            tex.Apply(updateMipmaps: true);

            for (var i = 3; i < flipped.Length; i += 4)
            {
                if (flipped[i] > 0) opaquePixels++;
            }
            return tex;
        }

        public void Dispose()
        {
            RenderPipelineManager.endFrameRendering -= OnEndFrame;
            m_UISystem.DestroyView(m_View);
            Object.Destroy(m_DummyCamera.gameObject);
        }
    }
}
