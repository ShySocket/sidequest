using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

/// <summary>
/// Proves the video actually reaches the screen.
/// </summary>
/// <remarks>
/// This exists because of a failure that produced no error at all: the
/// background shader had only a "UniversalForward" pass, while the project
/// renders through URP's 2D Renderer, which draws only "Universal2D". Everything
/// loaded, prepared and played correctly - and the screen stayed black.
///
/// Nothing short of reading rendered pixels catches that, so this renders the
/// scene camera into a texture and inspects it.
/// </remarks>
public class VideoBackgroundRenderTests
{
    const string SceneName = "VideoRunner";
    const float PrepareTimeout = 25f;

    [UnityTest]
    public IEnumerator VideoBackgroundRendersVisiblePixels()
    {
        SceneManager.LoadScene(SceneName);
        yield return null;

        var background = Object.FindFirstObjectByType<VideoBackground>();
        Assert.That(background, Is.Not.Null, "scene has no VideoBackground");

        float deadline = Time.realtimeSinceStartup + PrepareTimeout;
        while (!background.IsPrepared && Time.realtimeSinceStartup < deadline)
        {
            yield return null;
        }

        Assert.That(background.IsPrepared, Is.True,
            "VideoPlayer never prepared - check the clip is in StreamingAssets and decodable");

        // Wait in real time, not frames: a decoder needs wall clock, and under
        // batchmode a frame count elapses in microseconds.
        deadline = Time.realtimeSinceStartup + 10f;
        while (background.Player.frame <= 0 && Time.realtimeSinceStartup < deadline)
        {
            yield return null;
        }

        Assert.That(background.Player.isPlaying, Is.True, "VideoPlayer is not playing");
        Assert.That(background.Player.frame, Is.GreaterThan(0L), "no frames decoded");

        // A moment more so the first frame has certainly reached the texture.
        yield return new WaitForSecondsRealtime(0.5f);

        Texture2D shot = CaptureMainCamera();
        try
        {
            AssertLooksLikeVideo(shot);
        }
        finally
        {
            Object.DestroyImmediate(shot);
        }
    }

    static Texture2D CaptureMainCamera()
    {
        Camera camera = Camera.main;
        Assert.That(camera, Is.Not.Null, "no main camera");

        var target = new RenderTexture(320, 180, 24);
        RenderTexture previousTarget = camera.targetTexture;
        RenderTexture previousActive = RenderTexture.active;

        camera.targetTexture = target;
        camera.Render();

        RenderTexture.active = target;
        var shot = new Texture2D(target.width, target.height, TextureFormat.RGB24, false);
        shot.ReadPixels(new Rect(0, 0, target.width, target.height), 0, 0);
        shot.Apply();

        camera.targetTexture = previousTarget;
        RenderTexture.active = previousActive;
        target.Release();
        Object.DestroyImmediate(target);

        return shot;
    }

    static void AssertLooksLikeVideo(Texture2D shot)
    {
        Color32[] pixels = shot.GetPixels32();

        int lit = 0;
        int minimum = 255;
        int maximum = 0;
        long total = 0;

        foreach (Color32 pixel in pixels)
        {
            int luma = (pixel.r * 299 + pixel.g * 587 + pixel.b * 114) / 1000;
            if (luma > 12)
            {
                lit++;
            }

            minimum = Mathf.Min(minimum, luma);
            maximum = Mathf.Max(maximum, luma);
            total += luma;
        }

        float litFraction = (float)lit / pixels.Length;
        float mean = (float)total / pixels.Length;

        // The camera clears to black, so a background that never drew leaves an
        // almost entirely black frame. Daylight street footage does not.
        Assert.That(litFraction, Is.GreaterThan(0.5f),
            $"only {litFraction:P0} of the frame is non-black (mean luma {mean:0}); "
            + "the background is probably not being drawn by the active URP renderer");

        // A flat fill would also pass the test above; real footage has contrast.
        Assert.That(maximum - minimum, Is.GreaterThan(40),
            $"frame has almost no contrast (luma {minimum}..{maximum}); "
            + "the background may be drawing a solid colour rather than the video");
    }
}
