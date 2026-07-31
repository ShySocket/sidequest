using System.IO;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

/// <summary>
/// Covers the level file contract with the analyzer.
/// </summary>
/// <remarks>
/// The schema is shaped around JsonUtility's limits - no top-level arrays, no
/// arrays of arrays - so the first thing worth proving is that JsonUtility can
/// in fact read it. The rest covers the lookups the game runs every frame.
/// </remarks>
public class VideoLevelTests
{
    const string SampleJson = @"{
        ""version"": 2,
        ""source"": { ""file"": ""clip.mov"", ""fps"": 30, ""width"": 1920,
                      ""height"": 1080, ""duration"": 10 },
        ""playbackFile"": ""clip.play.mp4"",
        ""distanceUnits"": ""relative"",
        ""characterColumn"": 0.35,
        ""totalDistance"": 100,
        ""timeToDistance"": [ {""t"":0,""d"":0}, {""t"":5,""d"":50}, {""t"":10,""d"":100} ],
        ""path"": [ {""d"":0,""y"":0.5,""s"":""rail""}, {""d"":50,""y"":0.6,""s"":""floor""},
                    {""d"":100,""y"":0.7,""s"":""grass""} ],
        ""events"": [ {""id"":0,""type"":""jump"",""label"":""van"",""time"":5,
                       ""distance"":50,""window"":0.5,""windowDistance"":5} ],
        ""screenSpeed"": [ {""d"":0,""v"":0.4}, {""d"":50,""v"":0.6}, {""d"":100,""v"":0.5} ],
        ""hidden"": [ {""startDistance"":70,""endDistance"":80,
                       ""startTime"":7,""endTime"":8} ]
    }";

    static VideoLevel Sample() => VideoLevel.Parse(SampleJson);

    [Test]
    public void JsonUtilityReadsTheSchema()
    {
        VideoLevel level = Sample();

        Assert.That(level, Is.Not.Null);
        Assert.That(level.TotalDistance, Is.EqualTo(100f));
        Assert.That(level.CharacterColumn, Is.EqualTo(0.35f).Within(1e-4f));
        Assert.That(level.PlaybackFileName, Is.EqualTo("clip.play.mp4"));
        Assert.That(level.Events.Count, Is.EqualTo(1));
    }

    [Test]
    public void TimeAndDistanceRoundTrip()
    {
        VideoLevel level = Sample();

        for (float time = 0f; time <= 10f; time += 0.5f)
        {
            float recovered = level.TimeAtDistance(level.DistanceAtTime(time));
            Assert.That(recovered, Is.EqualTo(time).Within(1e-3f), $"at t={time}");
        }
    }

    [Test]
    public void DistanceLookupInterpolatesBetweenSamples()
    {
        Assert.That(Sample().DistanceAtTime(2.5f), Is.EqualTo(25f).Within(1e-3f));
    }

    [Test]
    public void LookupsClampOutsideTheClip()
    {
        VideoLevel level = Sample();

        Assert.That(level.DistanceAtTime(-5f), Is.EqualTo(0f));
        Assert.That(level.DistanceAtTime(999f), Is.EqualTo(100f));
        Assert.That(level.TimeAtDistance(-5f), Is.EqualTo(0f));
        Assert.That(level.TimeAtDistance(999f), Is.EqualTo(10f));
    }

    [Test]
    public void GroundIsInterpolatedAlongThePath()
    {
        VideoLevel level = Sample();

        Assert.That(level.GroundAtDistance(0f), Is.EqualTo(0.5f).Within(1e-4f));
        Assert.That(level.GroundAtDistance(25f), Is.EqualTo(0.55f).Within(1e-3f));
        Assert.That(level.GroundAtDistance(50f), Is.EqualTo(0.6f).Within(1e-4f));
    }

    [Test]
    public void HiddenSpansAreDetected()
    {
        VideoLevel level = Sample();

        Assert.That(level.IsHiddenAtDistance(60f), Is.False);
        Assert.That(level.IsHiddenAtDistance(75f), Is.True);
        Assert.That(level.IsHiddenAtDistance(85f), Is.False);
    }

    [Test]
    public void PlaybackFileFallsBackToTheAnalysedClip()
    {
        // Older levels predate the transcode step and name only one file.
        VideoLevel level = VideoLevel.Parse(
            SampleJson.Replace(@"""playbackFile"": ""clip.play.mp4"",", string.Empty));

        Assert.That(level.PlaybackFileName, Is.EqualTo("clip.mov"));
    }

    [Test]
    public void ScreenSpeedIsInterpolatedAlongTheLevel()
    {
        VideoLevel level = Sample();

        Assert.That(level.ScreenSpeedAtDistance(0f), Is.EqualTo(0.4f).Within(1e-4f));
        Assert.That(level.ScreenSpeedAtDistance(25f), Is.EqualTo(0.5f).Within(1e-3f));
        Assert.That(level.ScreenSpeedAtDistance(50f), Is.EqualTo(0.6f).Within(1e-4f));
    }

    [Test]
    public void ScreenSpeedIsZeroWhenTheLevelHasNoSpeedTrack()
    {
        // A ball that slides rather than rolls is visibly wrong, which beats
        // inventing a rate that quietly disagrees with the footage.
        VideoLevel level = VideoLevel.Parse(SampleJson.Replace("screenSpeed", "unusedTrack"));

        Assert.That(level.ScreenSpeedAtDistance(25f), Is.EqualTo(0f));
    }

    [Test]
    public void TravelDirectionIsNormalizedToPlusOrMinusOne()
    {
        Assert.That(Sample().TravelDirection, Is.EqualTo(-1));
        Assert.That(
            VideoLevel.Parse(SampleJson.Replace("\"characterColumn\": 0.35",
                "\"characterColumn\": 0.35, \"travelDirection\": 1")).TravelDirection,
            Is.EqualTo(1));
    }

    [Test]
    public void MalformedJsonIsRejectedRatherThanCrashing()
    {
        LogAssert.ignoreFailingMessages = true;
        Assert.That(VideoLevel.Parse("{}"), Is.Null);
        LogAssert.ignoreFailingMessages = false;
    }

    /// <summary>
    /// The real authored level, if it has been copied in. Guards against the
    /// analyzer and the game drifting apart - a schema change that breaks
    /// parsing would otherwise only show up on device.
    /// </summary>
    [Test]
    public void AuthoredLevelInStreamingAssetsParses()
    {
        string path = Path.Combine(Application.streamingAssetsPath, "IMG_3775.authored.json");
        if (!File.Exists(path))
        {
            Assert.Ignore("No authored level present; run copy-to-unity.sh.");
        }

        VideoLevel level = VideoLevel.Parse(File.ReadAllText(path));

        Assert.That(level, Is.Not.Null);
        Assert.That(level.TotalDistance, Is.GreaterThan(0f));
        Assert.That(level.Events.Count, Is.GreaterThan(0));
        Assert.That(level.Duration, Is.GreaterThan(0f));
        Assert.That(level.TravelDirection, Is.EqualTo(-1),
            "IMG_3775 was filmed with the world sweeping left to right");
        Assert.That(level.ScreenSpeedAtDistance(level.TotalDistance * 0.5f),
            Is.GreaterThan(0f), "level has no screen speed track; the ball would slide");

        // Every event must sit inside the level, or it can never be reached.
        foreach (VideoLevelEvent entry in level.Events)
        {
            Assert.That(entry.distance, Is.InRange(0f, level.TotalDistance), entry.label);
            Assert.That(entry.windowDistance, Is.GreaterThan(0f), entry.label);
        }

        // The ground line must stay on screen for the whole run.
        for (float d = 0f; d <= level.TotalDistance; d += level.TotalDistance / 200f)
        {
            Assert.That(level.GroundAtDistance(d), Is.InRange(0f, 1f), $"ground at d={d}");
        }
    }
}
