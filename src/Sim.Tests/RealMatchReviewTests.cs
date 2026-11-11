using System;
using System.IO;
using Sim.GodotShell;

namespace Sim.Tests;

public sealed class RealMatchReviewTests
{
    private static string Input(string contents)
    {
        var path = Path.GetTempFileName();
        File.WriteAllText(path, contents);
        return path;
    }

    [Fact]
    public void Patrol_PreservesEqualTimeMissingAndQuotedValues()
    {
        var path = Input("t,front,rear,left,right,alert,on_stage,action,ir1,ir2\n0,1,,3,4,0,1,\"left, then right\",5,6\n0,2,3,4,5,1,1,stop,,7\n");
        try
        {
            var recording = RealMatchReviewData.LoadRecording(path);
            Assert.Equal(2, recording.Events.Count);
            Assert.Equal("left, then right", recording.Events[0].Values["action"]);
            Assert.Null(recording.Events[0].Values["rear"]);
            Assert.Equal("stop", recording.Events[1].Values["action"]);
            Assert.Null(recording.Events[1].Values["ir1"]);
            Assert.Empty(RealMatchReviewData.ExtractPoses(recording));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void PosesFollowOriginalEventOrderAndNeverUseFutureValues()
    {
        var path = Input("{\"format\":\"real-match-v1\",\"events\":[" +
            "{\"time\":0,\"type\":\"sensor\",\"values\":{}}," +
            "{\"time\":1,\"type\":\"pose\",\"values\":{\"role\":\"us\",\"x\":1,\"y\":2,\"heading\":0.5,\"source\":\"measured\"}}," +
            "{\"time\":1,\"type\":\"pose\",\"values\":{\"role\":\"them\",\"x\":3,\"y\":4,\"heading\":-1,\"source\":\"synthetic\"}}," +
            "{\"time\":1,\"type\":\"pose\",\"values\":{\"role\":\"us\",\"x\":9,\"y\":8,\"heading\":2,\"source\":\"later\"}}]}");
        try
        {
            var poses = RealMatchReviewData.ExtractPoses(RealMatchReviewData.LoadRecording(path));
            Assert.Equal(3, poses.Count);
            Assert.Equal(new ReviewPose(1, 1, "us", 1, 2, 0.5, "measured"), poses[0]);
            Assert.Null(RealMatchReviewData.PoseAtOrBefore(poses, "us", 0));
            Assert.Null(RealMatchReviewData.PoseAtOrBefore(poses, "them", 1));
            Assert.Equal(poses[0], RealMatchReviewData.PoseAtOrBefore(poses, "us", 2));
            Assert.Equal(poses[1], RealMatchReviewData.PoseAtOrBefore(poses, "them", 2));
            Assert.Equal(poses[2], RealMatchReviewData.PoseAtOrBefore(poses, "us", 3));
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData("\"role\":\"us\",\"x\":\"NaN\",\"y\":2,\"heading\":0,\"source\":\"s\"", "x")]
    [InlineData("\"role\":\"us\",\"y\":2,\"heading\":0,\"source\":\"s\"", "x")]
    [InlineData("\"role\":\"other\",\"x\":1,\"y\":2,\"heading\":0,\"source\":\"s\"", "role")]
    [InlineData("\"role\":\"us\",\"x\":1,\"y\":2,\"heading\":0,\"source\":\" \"", "source")]
    [InlineData("\"role\":\"us\",\"x\":1,\"y\":2,\"source\":\"s\"", "heading")]
    [InlineData("\"role\":\"us\",\"x\":1,\"y\":2,\"heading\":\"Infinity\",\"source\":\"s\"", "heading")]
    public void InvalidPoseIsRejectedOnImport(string fields, string reason)
    {
        var path = Input("{\"format\":\"real-match-v1\",\"events\":[{\"time\":0,\"type\":\"pose\",\"values\":{" + fields + "}}]}");
        try { Assert.Contains(reason, Assert.Throws<FormatException>(() => RealMatchReviewData.LoadRecording(path)).Message); }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData("")]
    [InlineData("t,front,rear,left,right,alert,on_stage,action,ir1,ir2\n")]
    [InlineData("{\"format\":\"real-match-v1\",\"events\":[]}")]
    [InlineData("t,front,rear,left,right,alert,on_stage,action,ir1,ir2\n2,1,2,3,4,0,1,a,1,2\n1,1,2,3,4,0,1,a,1,2\n")]
    [InlineData("{\"format\":\"real-match-v1\",\"events\":[{\"time\":1e999,\"type\":\"sensor\",\"values\":{}}]}")]
    public void InvalidRecordingIsRejected(string contents)
    {
        var path = Input(contents);
        try { Assert.ThrowsAny<Exception>(() => RealMatchReviewData.LoadRecording(path)); }
        finally { File.Delete(path); }
    }

    [Fact]
    public void SeriesReference_RoundTripsInclusiveStatisticsAndSource()
    {
        var input = Input("t,left,right,valid\n0,1,,1\n0,3,,\n1,99,2,0\n");
        var output = Path.GetTempFileName();
        try
        {
            var series = RealMatchReviewData.LoadSeries(input);
            var reference = RealMatchReviewData.MakeReference(series, "point", 0, 1, 1.2, -2, 0.4);
            Assert.Equal(new ReviewStats(2, 1, 3, 2), reference.Stats["left"]);
            Assert.Equal(new ReviewStats(0, 0, 0, 0), reference.Stats["right"]);
            Assert.Equal(new ReviewStats(1, 1, 1, 1), reference.Stats["valid"]);
            RealMatchReviewData.SaveReferences(output, new[] { reference });
            var loaded = Assert.Single(RealMatchReviewData.LoadReferences(output));
            Assert.Equal(input, loaded.Source);
            Assert.Equal(1.2, loaded.X);
            Assert.Equal(reference.Stats["left"], loaded.Stats["left"]);
            var saved = File.ReadAllText(output);
            Assert.ThrowsAny<Exception>(() => RealMatchReviewData.SaveReferences(output, new[] { reference with { X = double.NaN } }));
            Assert.Equal(saved, File.ReadAllText(output));
            Assert.ThrowsAny<Exception>(() => RealMatchReviewData.MakeReference(series, "point", 1, 0, 0, 0, 0));
            Assert.ThrowsAny<Exception>(() => RealMatchReviewData.MakeReference(series, "point", 0, 1, double.NaN, 0, 0));
        }
        finally { File.Delete(input); File.Delete(output); }
    }

    [Fact]
    public void JsonScalarValuesAndReferenceRequiredFieldsAreValidated()
    {
        var input = Input("{\"format\":\"real-match-v1\",\"events\":[{\"time\":0,\"type\":\"sensor\",\"values\":{\"raw\":123,\"missing\":null}},{\"time\":0,\"type\":\"motor\",\"values\":{\"enabled\":true}}]}");
        var bad = Input("{\"format\":\"real-match-references-v1\",\"references\":[{\"name\":\"p\",\"source\":\"s\",\"startIndex\":0,\"endIndex\":0,\"y\":0,\"heading\":0,\"stats\":{}}]}");
        try
        {
            var events = RealMatchReviewData.LoadRecording(input).Events;
            Assert.Equal("123", events[0].Values["raw"]);
            Assert.Null(events[0].Values["missing"]);
            Assert.Equal("motor", events[1].Type);
            Assert.ThrowsAny<Exception>(() => RealMatchReviewData.LoadReferences(bad));
        }
        finally { File.Delete(input); File.Delete(bad); }
    }
}
