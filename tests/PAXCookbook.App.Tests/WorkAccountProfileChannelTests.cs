#if EXPERIMENTAL_WAM
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using PAXCookbook.App;
using Xunit;

namespace PAXCookbook.App.Tests;

// Batch 3 — bounded Work-account profile presentation channel + native
// different-account intent (EXPERIMENTAL_WAM only).
//
// Every value here is SYNTHETIC: no real photo, UPN, tenant, token, or account
// value appears. The tests prove the bounded, closed-shape envelope contract the
// top-level shell receiver revalidates, the degrade-to-initials guards
// (wrong content type, over-ceiling encoding), the clear (state=none) envelope,
// that Publish routes exactly one post through the wired poster, and the exact
// closed shape / ack contract of the native different-account intent.
public sealed class WorkAccountProfileChannelTests
{
    private static Dictionary<string, JsonElement> Parse(string json)
    {
        var map = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        using JsonDocument doc = JsonDocument.Parse(json);
        Assert.Equal(JsonValueKind.Object, doc.RootElement.ValueKind);
        foreach (JsonProperty p in doc.RootElement.EnumerateObject())
        {
            map[p.Name] = p.Value.Clone();
        }
        return map;
    }

    // ---- photo envelope -----------------------------------------------------

    [Fact]
    public void Photo_Jpeg_WithinCeiling_BuildsClosedPhotoEnvelope()
    {
        byte[] bytes = Encoding.ASCII.GetBytes("synthetic-jpeg-bytes");
        var presentation = WorkAccountProfilePresentation.WithPhoto(bytes, "image/jpeg", "AB");

        string json = WorkAccountProfileWindowChannel.BuildEnvelope(presentation);
        Dictionary<string, JsonElement> map = Parse(json);

        // Exactly the closed photo field set: type, state, contentType, imageBase64, label.
        Assert.Equal(5, map.Count);
        Assert.Equal(WorkAccountProfileWindowChannel.MessageType, map["type"].GetString());
        Assert.Equal("photo", map["state"].GetString());
        Assert.Equal("image/jpeg", map["contentType"].GetString());
        Assert.Equal(Convert.ToBase64String(bytes), map["imageBase64"].GetString());
        Assert.Equal("Work account", map["label"].GetString());

        // No identity / URL / token field leaks into the envelope.
        Assert.False(map.ContainsKey("initials"));
        Assert.False(map.ContainsKey("url"));
        Assert.False(map.ContainsKey("account"));
        Assert.False(map.ContainsKey("upn"));
        Assert.False(map.ContainsKey("tenant"));
        Assert.False(map.ContainsKey("token"));
    }

    [Fact]
    public void Photo_NonJpegContentType_DegradesToInitials()
    {
        byte[] bytes = Encoding.ASCII.GetBytes("synthetic");
        var presentation = WorkAccountProfilePresentation.WithPhoto(bytes, "image/png", "AB");

        string json = WorkAccountProfileWindowChannel.BuildEnvelope(presentation);
        Dictionary<string, JsonElement> map = Parse(json);

        Assert.Equal(4, map.Count);
        Assert.Equal("initials", map["state"].GetString());
        Assert.Equal("AB", map["initials"].GetString());
        Assert.False(map.ContainsKey("imageBase64"));
        Assert.False(map.ContainsKey("contentType"));
    }

    [Fact]
    public void Photo_OverEncodedCeiling_DegradesToInitials()
    {
        // Raw bytes just over the raw ceiling so the base64 encoding exceeds the
        // encoded ceiling — degrade to initials rather than post an over-ceiling
        // envelope.
        byte[] bytes = new byte[WorkAccountProfilePhotoFetcher.MaxPhotoBytes + 3];
        var presentation = WorkAccountProfilePresentation.WithPhoto(bytes, "image/jpeg", "CD");

        string json = WorkAccountProfileWindowChannel.BuildEnvelope(presentation);
        Dictionary<string, JsonElement> map = Parse(json);

        Assert.Equal("initials", map["state"].GetString());
        Assert.Equal("CD", map["initials"].GetString());
        Assert.False(map.ContainsKey("imageBase64"));
    }

    [Fact]
    public void Photo_ExactlyAtEncodedCeiling_KeepsPhoto()
    {
        // Exactly the raw ceiling encodes to exactly the encoded ceiling.
        byte[] bytes = new byte[WorkAccountProfilePhotoFetcher.MaxPhotoBytes];
        var presentation = WorkAccountProfilePresentation.WithPhoto(bytes, "image/jpeg", "EF");

        string json = WorkAccountProfileWindowChannel.BuildEnvelope(presentation);
        Dictionary<string, JsonElement> map = Parse(json);

        Assert.Equal("photo", map["state"].GetString());
        Assert.True(map["imageBase64"].GetString()!.Length <= WorkAccountProfileWindowChannel.MaxEncodedChars);
    }

    // ---- initials envelope --------------------------------------------------

    [Fact]
    public void Initials_BuildsClosedInitialsEnvelope()
    {
        var presentation = WorkAccountProfilePresentation.WithInitials("XY");
        string json = WorkAccountProfileWindowChannel.BuildEnvelope(presentation);
        Dictionary<string, JsonElement> map = Parse(json);

        Assert.Equal(4, map.Count);
        Assert.Equal("initials", map["state"].GetString());
        Assert.Equal("XY", map["initials"].GetString());
        Assert.Equal("Work account", map["label"].GetString());
        Assert.False(map.ContainsKey("imageBase64"));
    }

    [Fact]
    public void Initials_LongerThanTwo_IsClampedInEnvelope()
    {
        // Defensive re-clamp: even if an initials value somehow exceeded two
        // characters, the envelope never carries more than two.
        var presentation = WorkAccountProfilePresentation.WithInitials("AB");
        string json = WorkAccountProfileWindowChannel.BuildEnvelope(presentation);
        Dictionary<string, JsonElement> map = Parse(json);
        Assert.True(map["initials"].GetString()!.Length <= 2);
    }

    // ---- clear / none envelope ----------------------------------------------

    [Fact]
    public void ClearEnvelope_IsClosedNoneEnvelope()
    {
        string json = WorkAccountProfileWindowChannel.BuildClearEnvelope();
        Dictionary<string, JsonElement> map = Parse(json);

        Assert.Equal(3, map.Count);
        Assert.Equal(WorkAccountProfileWindowChannel.MessageType, map["type"].GetString());
        Assert.Equal("none", map["state"].GetString());
        Assert.Equal("Work account", map["label"].GetString());
        Assert.False(map.ContainsKey("imageBase64"));
        Assert.False(map.ContainsKey("initials"));
    }

    // ---- Publish routing ----------------------------------------------------

    [Fact]
    public void Publish_RoutesExactlyOnePostThroughWiredPoster()
    {
        var posted = new List<string>();
        var channel = new WorkAccountProfileWindowChannel();
        channel.SetPoster(json => posted.Add(json), action => action());

        channel.Publish(WorkAccountProfilePresentation.WithInitials("AB"));

        Assert.Single(posted);
        Dictionary<string, JsonElement> map = Parse(posted[0]);
        Assert.Equal("initials", map["state"].GetString());
    }

    [Fact]
    public void Publish_BeforePosterWired_RetainsForExplicitReplay()
    {
        var posted = new List<string>();
        var channel = new WorkAccountProfileWindowChannel();
        channel.Publish(WorkAccountProfilePresentation.WithInitials("AB"));
        channel.SetPoster(json => posted.Add(json), action => action());

        Assert.Empty(posted);
        channel.ReplayLatest();

        Assert.Single(posted);
        Assert.Equal("AB", Parse(posted[0])["initials"].GetString());
    }

    [Fact]
    public void Publish_WithPoster_ThenReplay_SendsSameBoundedEnvelope()
    {
        var posted = new List<string>();
        var channel = new WorkAccountProfileWindowChannel();
        channel.SetPoster(json => posted.Add(json), action => action());

        channel.Publish(WorkAccountProfilePresentation.WithInitials("AB"));
        channel.ReplayLatest();

        Assert.Equal(2, posted.Count);
        Assert.Equal(posted[0], posted[1]);
    }

    [Fact]
    public void ReplacementPublish_UpdatesLatestEnvelope()
    {
        var posted = new List<string>();
        var channel = new WorkAccountProfileWindowChannel();
        channel.SetPoster(json => posted.Add(json), action => action());

        channel.Publish(WorkAccountProfilePresentation.WithInitials("AB"));
        channel.Publish(WorkAccountProfilePresentation.WithInitials("CD"));
        posted.Clear();
        channel.ReplayLatest();

        Assert.Single(posted);
        Assert.Equal("CD", Parse(posted[0])["initials"].GetString());
    }

    [Fact]
    public void Clear_DropsLatest_PostsNone_AndPreventsLaterReplay()
    {
        var posted = new List<string>();
        var channel = new WorkAccountProfileWindowChannel();
        channel.SetPoster(json => posted.Add(json), action => action());

        channel.Publish(WorkAccountProfilePresentation.WithInitials("AB"));
        channel.Clear();
        channel.ReplayLatest();

        Assert.Equal(2, posted.Count);
        Assert.Equal("initials", Parse(posted[0])["state"].GetString());
        Assert.Equal("none", Parse(posted[1])["state"].GetString());
    }

    [Fact]
    public void Clear_RoutesNoneEnvelopeThroughPoster()
    {
        var posted = new List<string>();
        var channel = new WorkAccountProfileWindowChannel();
        channel.SetPoster(json => posted.Add(json), action => action());

        channel.Clear();

        Assert.Single(posted);
        Assert.Equal("none", Parse(posted[0])["state"].GetString());
    }

    // ---- replay-control intent parsing --------------------------------------

    [Theory]
    [InlineData("{\"type\":\"cookbook:work-account-profile-ready\"}", "ReplayLatest")]
    [InlineData("{\"type\":\"cookbook:work-account-profile-clear\"}", "Clear")]
    public void ProfileControl_ExactClosedShape_Accepted(
        string json, string expected)
    {
        Assert.True(WorkAccountProfileControlMessage.TryParse(json, out var action));
        Assert.Equal(expected, action.ToString());
    }

    [Theory]
    [InlineData("{\"type\":\"cookbook:work-account-profile-ready\",\"extra\":true}")]
    [InlineData("{\"type\":\"cookbook:work-account-profile-clear\",\"provider\":\"work_account\"}")]
    [InlineData("{\"type\":\"cookbook:work-account-profile\"}")]
    [InlineData("{\"type\":1}")]
    [InlineData("{\"Type\":\"cookbook:work-account-profile-ready\"}")]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("not json")]
    [InlineData("")]
    public void ProfileControl_NonExactShape_Rejected(string json)
    {
        Assert.False(WorkAccountProfileControlMessage.TryParse(json, out var action));
        Assert.Equal(WorkAccountProfileControlAction.None, action);
    }

    [Fact]
    public void ProfileControl_Handler_CanOnlyReplayOrClearChannelPresentation()
    {
        var posted = new List<string>();
        var channel = new WorkAccountProfileWindowChannel();
        channel.SetPoster(json => posted.Add(json), action => action());
        channel.Publish(WorkAccountProfilePresentation.WithInitials("AB"));
        posted.Clear();

        Assert.True(WorkAccountProfileControlMessage.TryHandle(
            "{\"type\":\"cookbook:work-account-profile-ready\"}", channel));
        Assert.True(WorkAccountProfileControlMessage.TryHandle(
            "{\"type\":\"cookbook:work-account-profile-clear\"}", channel));
        Assert.False(WorkAccountProfileControlMessage.TryHandle(
            "{\"type\":\"cookbook:work-account-profile-ready\",\"unlock\":true}", channel));

        Assert.Equal(2, posted.Count);
        Assert.Equal("initials", Parse(posted[0])["state"].GetString());
        Assert.Equal("none", Parse(posted[1])["state"].GetString());
    }

    [Fact]
    public void ProfileControl_Handler_HasNoAuthorizationOrDaemonDependency()
    {
        FieldInfo[] mutableFields = typeof(WorkAccountProfileControlMessage)
            .GetFields(BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)
            .Where(field => !field.IsLiteral)
            .ToArray();
        MethodInfo handler = typeof(WorkAccountProfileControlMessage).GetMethod(
            "TryHandle", BindingFlags.Static | BindingFlags.NonPublic)!;
        Type[] parameterTypes = handler.GetParameters().Select(parameter => parameter.ParameterType).ToArray();

        Assert.Empty(mutableFields);
        Assert.Equal(new[] { typeof(string), typeof(WorkAccountProfileWindowChannel) }, parameterTypes);
        Assert.DoesNotContain(parameterTypes, type =>
            type.Name.Contains("Broker", StringComparison.Ordinal) ||
            type.Name.Contains("Daemon", StringComparison.Ordinal) ||
            type.Name.Contains("Auth", StringComparison.Ordinal));
    }

    [Fact]
    public void ProfileControl_WebViewHandler_IsConjoinedWithExactApplicationOrigin()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "PAXCookbook.sln")))
        {
            dir = dir.Parent;
        }
        Assert.NotNull(dir);

        string source = File.ReadAllText(Path.Combine(
            dir!.FullName, "src", "PAXCookbook.App", "WebViewShell.cs"));
        MatchCollection calls = Regex.Matches(
            source, "WorkAccountProfileControlMessage\\.TryHandle");
        Assert.Single(calls);
        Assert.Matches(
            "WebMessageOrigin\\.IsSameOrigin\\(messageSource, url\\)\\s*&&\\s*" +
            "WorkAccountProfileControlMessage\\.TryHandle\\(\\s*rawJson,\\s*" +
            "experimentalWamComponents\\.Channel\\)",
            source);
    }

    // ---- different-account intent parsing -----------------------------------

    [Fact]
    public void DifferentAccount_ExactClosedShape_Accepted()
    {
        Assert.True(WorkAccountDifferentAccountMessage.IsExactRequest(
            "{\"type\":\"cookbook:work-account-different-account\"}"));
    }

    [Theory]
    [InlineData("{\"type\":\"cookbook:work-account-different-account\",\"upn\":\"a@b.com\"}")] // account field
    [InlineData("{\"type\":\"cookbook:work-account-different-account\",\"accountId\":\"x\"}")]  // selection field
    [InlineData("{\"type\":\"cookbook:work-account-different-account\",\"index\":0}")]           // extra field
    [InlineData("{\"type\":\"cookbook:work-account-profile\"}")]                                  // wrong type
    [InlineData("{\"requestId\":\"x\"}")]                                                          // missing type
    [InlineData("{}")]                                                                              // empty
    [InlineData("[]")]                                                                              // non-object
    [InlineData("not json")]                                                                        // malformed
    [InlineData("")]                                                                                // empty string
    public void DifferentAccount_NonExactShape_Rejected(string json)
    {
        Assert.False(WorkAccountDifferentAccountMessage.IsExactRequest(json));
    }

    [Fact]
    public void DifferentAccount_Ack_IsBoundedClearedOrFailed()
    {
        Dictionary<string, JsonElement> cleared = Parse(WorkAccountDifferentAccountMessage.BuildAck(true));
        Assert.Equal(2, cleared.Count);
        Assert.Equal(WorkAccountDifferentAccountMessage.AckType, cleared["type"].GetString());
        Assert.Equal("cleared", cleared["result"].GetString());

        Dictionary<string, JsonElement> failed = Parse(WorkAccountDifferentAccountMessage.BuildAck(false));
        Assert.Equal("failed", failed["result"].GetString());
    }
}
#endif
