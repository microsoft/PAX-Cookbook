#if EXPERIMENTAL_WAM
using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using PAXCookbook.App;
using Xunit;

namespace PAXCookbook.App.Tests;

// Batch 1 — native Work-account profile-photo core (EXPERIMENTAL_WAM only).
// All bytes and tokens here are SYNTHETIC; no real token, photo, or identifier
// appears. A synthetic HttpMessageHandler stands in for the network so the exact
// request policy, streaming cap, content-type gating, and outcome mapping are
// proven deterministically without any live Graph call.
public sealed class WorkAccountProfilePhotoTests
{
    private const string SyntheticToken = "SYNTHETIC.dummy.token.value";

    private static readonly byte[] MinimalJpeg =
        { 0xFF, 0xD8, 0xFF, 0x00, 0x11, 0x22, 0xFF, 0xD9 };

    private static readonly string[] ForbiddenMemberFragments =
    {
        "token", "secret", "password", "assertion", "bearer", "credential",
    };

    // ---- synthetic transport ------------------------------------------------

    private sealed class FuncHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;
        internal HttpMethod? SeenMethod { get; private set; }
        internal Uri? SeenUri { get; private set; }
        internal AuthenticationHeaderValue? SeenAuthorization { get; private set; }
        internal bool SeenHasContent { get; private set; }

        internal FuncHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) => _responder = responder;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            SeenMethod = request.Method;
            SeenUri = request.RequestUri;
            SeenAuthorization = request.Headers.Authorization;
            SeenHasContent = request.Content is not null;
            return Task.FromResult(_responder(request));
        }
    }

    private sealed class ThrowHandler : HttpMessageHandler
    {
        private readonly Exception _ex;
        internal ThrowHandler(Exception ex) => _ex = ex;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => throw _ex;
    }

    // A read-only stream that can serve up to Size bytes and records how many
    // bytes were actually served, so a test can prove the fetcher stopped early
    // instead of buffering the whole body.
    private sealed class CountingStream : System.IO.Stream
    {
        private long _remaining;
        internal long BytesServed { get; private set; }
        internal CountingStream(long size) => _remaining = size;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_remaining <= 0) return 0;
            int n = (int)Math.Min(count, _remaining);
            Array.Clear(buffer, offset, n);
            _remaining -= n;
            BytesServed += n;
            return n;
        }
        public override long Seek(long offset, System.IO.SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private static HttpResponseMessage Ok(byte[] bytes, string contentType)
    {
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
    }

    private static HttpResponseMessage Status(HttpStatusCode code) =>
        new HttpResponseMessage(code) { Content = new ByteArrayContent(Array.Empty<byte>()) };

    private static async Task<WorkAccountPhotoFetchResult> FetchWith(
        Func<HttpRequestMessage, HttpResponseMessage> responder, Action<FuncHandler>? inspect = null)
    {
        var handler = new FuncHandler(responder);
        using var fetcher = new WorkAccountProfilePhotoFetcher(handler);
        WorkAccountPhotoFetchResult r = await fetcher.FetchAsync(SyntheticToken, CancellationToken.None);
        inspect?.Invoke(handler);
        return r;
    }

    // ---- request policy (rejected BEFORE any network call) ------------------

    [Fact]
    public void Policy_ExactEndpoint_IsAccepted()
    {
        Assert.True(WorkAccountGraphPhotoRequest.IsAllowed(
            "GET", WorkAccountGraphPhotoRequest.Endpoint, hasBody: false));
        Assert.Equal("https", WorkAccountGraphPhotoRequest.Endpoint.Scheme);
        Assert.Equal("graph.microsoft.com", WorkAccountGraphPhotoRequest.Endpoint.Host);
        Assert.Equal("/v1.0/me/photo/$value", WorkAccountGraphPhotoRequest.Endpoint.AbsolutePath);
        Assert.True(string.IsNullOrEmpty(WorkAccountGraphPhotoRequest.Endpoint.Query));
    }

    [Fact]
    public void Policy_WrongHost_Rejected()
    {
        Assert.False(WorkAccountGraphPhotoRequest.IsAllowed(
            "GET", new Uri("https://evil.example.com/v1.0/me/photo/$value"), hasBody: false));
        Assert.False(WorkAccountGraphPhotoRequest.IsAllowed(
            "GET", new Uri("https://graph.microsoft.com.evil.example.com/v1.0/me/photo/$value"), hasBody: false));
    }

    [Fact]
    public void Policy_NonHttpsScheme_Rejected()
    {
        Assert.False(WorkAccountGraphPhotoRequest.IsAllowed(
            "GET", new Uri("http://graph.microsoft.com/v1.0/me/photo/$value"), hasBody: false));
    }

    [Fact]
    public void Policy_WrongPath_Rejected()
    {
        Assert.False(WorkAccountGraphPhotoRequest.IsAllowed(
            "GET", new Uri("https://graph.microsoft.com/v1.0/me"), hasBody: false));
        Assert.False(WorkAccountGraphPhotoRequest.IsAllowed(
            "GET", new Uri("https://graph.microsoft.com/v1.0/me/messages"), hasBody: false));
        Assert.False(WorkAccountGraphPhotoRequest.IsAllowed(
            "GET", new Uri("https://graph.microsoft.com/beta/me/photo/$value"), hasBody: false));
    }

    [Fact]
    public void Policy_Query_Rejected()
    {
        Assert.False(WorkAccountGraphPhotoRequest.IsAllowed(
            "GET", new Uri("https://graph.microsoft.com/v1.0/me/photo/$value?x=1"), hasBody: false));
    }

    [Fact]
    public void Policy_NonGet_Rejected_AndBuildIsAlwaysGet()
    {
        Assert.False(WorkAccountGraphPhotoRequest.IsAllowed(
            "POST", WorkAccountGraphPhotoRequest.Endpoint, hasBody: false));
        Assert.False(WorkAccountGraphPhotoRequest.IsAllowed(
            "PUT", WorkAccountGraphPhotoRequest.Endpoint, hasBody: false));

        using HttpRequestMessage? built = WorkAccountGraphPhotoRequest.Build();
        Assert.NotNull(built);
        Assert.Equal(HttpMethod.Get, built!.Method);
        Assert.Equal(WorkAccountGraphPhotoRequest.Endpoint, built.RequestUri);
        Assert.Null(built.Content);
    }

    [Fact]
    public void Policy_Body_Rejected()
    {
        Assert.False(WorkAccountGraphPhotoRequest.IsAllowed(
            "GET", WorkAccountGraphPhotoRequest.Endpoint, hasBody: true));
    }

    // ---- outcome mapping ----------------------------------------------------

    [Fact]
    public async Task Fetch_200Jpeg_Available_AndSendsExactBearerGet()
    {
        FuncHandler? seen = null;
        WorkAccountPhotoFetchResult r = await FetchWith(
            _ => Ok(MinimalJpeg, "image/jpeg"),
            h => seen = h);

        Assert.Equal(WorkAccountPhotoOutcome.Available, r.Outcome);
        Assert.NotNull(r.ImageBytes);
        Assert.Equal(MinimalJpeg, r.ImageBytes);
        Assert.Equal("image/jpeg", r.ContentType);

        Assert.NotNull(seen);
        Assert.Equal(HttpMethod.Get, seen!.SeenMethod);
        Assert.Equal(WorkAccountGraphPhotoRequest.Endpoint, seen.SeenUri);
        Assert.False(seen.SeenHasContent);
        Assert.NotNull(seen.SeenAuthorization);
        Assert.Equal("Bearer", seen.SeenAuthorization!.Scheme);
        Assert.Equal(SyntheticToken, seen.SeenAuthorization.Parameter);
    }

    [Fact]
    public async Task Fetch_302_Redirected_NotFollowed()
    {
        WorkAccountPhotoFetchResult r = await FetchWith(_ =>
        {
            var resp = Status(HttpStatusCode.Redirect);
            resp.Headers.Location = new Uri("https://evil.example.com/steal");
            return resp;
        });
        Assert.Equal(WorkAccountPhotoOutcome.Redirected, r.Outcome);
        Assert.Null(r.ImageBytes);
    }

    [Fact]
    public async Task Fetch_404_NotFound()
    {
        WorkAccountPhotoFetchResult r = await FetchWith(_ => Status(HttpStatusCode.NotFound));
        Assert.Equal(WorkAccountPhotoOutcome.NotFound, r.Outcome);
    }

    [Fact]
    public async Task Fetch_403_And_401_Forbidden()
    {
        Assert.Equal(WorkAccountPhotoOutcome.Forbidden,
            (await FetchWith(_ => Status(HttpStatusCode.Forbidden))).Outcome);
        Assert.Equal(WorkAccountPhotoOutcome.Forbidden,
            (await FetchWith(_ => Status(HttpStatusCode.Unauthorized))).Outcome);
    }

    [Fact]
    public async Task Fetch_Timeout_MapsToTimeout()
    {
        var handler = new ThrowHandler(new TaskCanceledException());
        using var fetcher = new WorkAccountProfilePhotoFetcher(handler);
        WorkAccountPhotoFetchResult r = await fetcher.FetchAsync(SyntheticToken, CancellationToken.None);
        Assert.Equal(WorkAccountPhotoOutcome.Timeout, r.Outcome);
    }

    [Fact]
    public async Task Fetch_WrongContentType_Rejected_IncludingPng()
    {
        Assert.Equal(WorkAccountPhotoOutcome.WrongContentType,
            (await FetchWith(_ => Ok(MinimalJpeg, "image/png"))).Outcome);
        Assert.Equal(WorkAccountPhotoOutcome.WrongContentType,
            (await FetchWith(_ => Ok(MinimalJpeg, "application/json"))).Outcome);
        Assert.Equal(WorkAccountPhotoOutcome.WrongContentType,
            (await FetchWith(_ => Ok(MinimalJpeg, "text/html"))).Outcome);
    }

    [Fact]
    public async Task Fetch_OverCeiling_RejectsEarly_WithoutOverBuffering()
    {
        long size = 8L * 1024 * 1024; // far larger than the 2 MiB ceiling
        var stream = new CountingStream(size);
        var content = new StreamContent(stream);
        content.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");

        var handler = new FuncHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        using var fetcher = new WorkAccountProfilePhotoFetcher(handler);

        WorkAccountPhotoFetchResult r = await fetcher.FetchAsync(SyntheticToken, CancellationToken.None);

        Assert.Equal(WorkAccountPhotoOutcome.TooLarge, r.Outcome);
        Assert.Null(r.ImageBytes);
        // Stopped early: never read the whole body, and never buffered past the
        // ceiling plus one bounded read chunk.
        Assert.True(stream.BytesServed < size, "fetcher read the entire oversized body");
        Assert.True(stream.BytesServed <= WorkAccountProfilePhotoFetcher.MaxPhotoBytes + (128 * 1024),
            $"fetcher over-buffered: served {stream.BytesServed} bytes");
    }

    [Fact]
    public async Task Fetch_MalformedOrTruncatedJpeg_FallsBack()
    {
        // Truncated: SOI present, EOI missing.
        Assert.Equal(WorkAccountPhotoOutcome.MalformedResponse,
            (await FetchWith(_ => Ok(new byte[] { 0xFF, 0xD8, 0xFF, 0x00 }, "image/jpeg"))).Outcome);
        // Non-JPEG bytes served under a jpeg content type (PNG signature).
        Assert.Equal(WorkAccountPhotoOutcome.MalformedResponse,
            (await FetchWith(_ => Ok(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A }, "image/jpeg"))).Outcome);
    }

    [Fact]
    public async Task Fetch_TransportException_MapsToTransportFailure_AndDoesNotThrow()
    {
        var handler = new ThrowHandler(new HttpRequestException("connection refused"));
        using var fetcher = new WorkAccountProfilePhotoFetcher(handler);
        WorkAccountPhotoFetchResult r = await fetcher.FetchAsync(SyntheticToken, CancellationToken.None);
        Assert.Equal(WorkAccountPhotoOutcome.TransportFailure, r.Outcome);
    }

    [Fact]
    public async Task Fetch_EmptyToken_FailsClosed_WithNoNetworkCall()
    {
        var handler = new FuncHandler(_ => throw new InvalidOperationException("network must not be reached"));
        using var fetcher = new WorkAccountProfilePhotoFetcher(handler);
        WorkAccountPhotoFetchResult r = await fetcher.FetchAsync(string.Empty, CancellationToken.None);
        Assert.Equal(WorkAccountPhotoOutcome.TransportFailure, r.Outcome);
        Assert.Null(handler.SeenUri);
    }

    // ---- hardened handler ---------------------------------------------------

    [Fact]
    public void HardenedHandler_HasSafeFlags_AndNoLoggingWrapper()
    {
        HttpClientHandler h = WorkAccountProfilePhotoFetcher.CreateHardenedHandler();
        Assert.False(h.AllowAutoRedirect);
        Assert.False(h.UseCookies);
        Assert.False(h.UseDefaultCredentials);
        Assert.Null(h.Credentials);
        Assert.False(h.PreAuthenticate);
        // A plain HttpClientHandler — no DelegatingHandler wrapper that could log
        // the Authorization header or URL.
        Assert.IsType<HttpClientHandler>(h);
    }

    // ---- presentation factory (photo failure preserves the flow) ------------

    [Fact]
    public void Presentation_AvailablePhoto_YieldsPhotoState()
    {
        var photo = WorkAccountPhotoFetchResult.Available(MinimalJpeg, "image/jpeg");
        WorkAccountProfilePresentation p =
            WorkAccountProfilePresentationFactory.Build(photo, "Brian Middendorf");

        Assert.Equal(WorkAccountProfileState.Photo, p.State);
        Assert.Equal(MinimalJpeg, p.ImageBytes);
        Assert.Equal("image/jpeg", p.ContentType);
        Assert.Equal("BM", p.Initials);
        Assert.True(p.IsWorkAccountLabel);
    }

    [Fact]
    public void Presentation_AnyPhotoFailure_DegradesToInitials()
    {
        WorkAccountPhotoOutcome[] failures =
        {
            WorkAccountPhotoOutcome.NotFound,
            WorkAccountPhotoOutcome.Forbidden,
            WorkAccountPhotoOutcome.Timeout,
            WorkAccountPhotoOutcome.Redirected,
            WorkAccountPhotoOutcome.WrongContentType,
            WorkAccountPhotoOutcome.TooLarge,
            WorkAccountPhotoOutcome.TransportFailure,
            WorkAccountPhotoOutcome.MalformedResponse,
        };

        foreach (WorkAccountPhotoOutcome outcome in failures)
        {
            var photo = WorkAccountPhotoFetchResult.Failure(outcome);
            WorkAccountProfilePresentation p =
                WorkAccountProfilePresentationFactory.Build(photo, "brian.middendorf@contoso.com");

            Assert.Equal(WorkAccountProfileState.Initials, p.State);
            Assert.Null(p.ImageBytes);
            Assert.Null(p.ContentType);
            Assert.Equal("BM", p.Initials);
            Assert.True(p.IsWorkAccountLabel);
        }
    }

    [Fact]
    public void Presentation_NullPhoto_DegradesToInitials()
    {
        WorkAccountProfilePresentation p =
            WorkAccountProfilePresentationFactory.Build(null, "Ada Lovelace");
        Assert.Equal(WorkAccountProfileState.Initials, p.State);
        Assert.Equal("AL", p.Initials);
    }

    // ---- initials deriver ---------------------------------------------------

    [Theory]
    [InlineData("Brian Middendorf", "BM")]
    [InlineData("brian.middendorf@contoso.com", "BM")]
    [InlineData("brian@contoso.com", "BR")]
    [InlineData("Ada Lovelace", "AL")]
    [InlineData("user123", "US")]
    [InlineData("a", "A")]
    [InlineData("josé garcía", "JG")]
    [InlineData("  jane   doe  ", "JD")]
    [InlineData("O'Brien Smith", "OB")]
    public void Initials_Derives_UpTo_TwoUppercaseLetters(string source, string expected)
    {
        Assert.Equal(expected, WorkAccountInitialsDeriver.Derive(source));
    }

    [Theory]
    [InlineData("@contoso.com")]
    [InlineData("   ")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("!!!")]
    [InlineData("123 456")]
    public void Initials_Fallback_WhenNoUsableLetter(string? source)
    {
        Assert.Equal("WA", WorkAccountInitialsDeriver.Derive(source));
    }

    [Fact]
    public void Initials_NeverExposeSourceOrDomain_AndAreBounded()
    {
        string result = WorkAccountInitialsDeriver.Derive("alexandra.jones@contoso.onmicrosoft.com");
        Assert.Equal("AJ", result);
        Assert.True(result.Length <= 2);
        Assert.DoesNotContain("@", result);
        Assert.DoesNotContain("contoso", result, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("alexandra", result, StringComparison.OrdinalIgnoreCase);
        Assert.All(result, c => Assert.True(char.IsLetter(c) && char.IsUpper(c)));
    }

    // ---- containment: no token anywhere on the new contracts ----------------

    [Fact]
    public void NewContracts_HaveNoTokenBearingMembers()
    {
        Type[] types =
        {
            typeof(WorkAccountPhotoFetchResult),
            typeof(WorkAccountProfilePresentation),
        };

        foreach (Type t in types)
        {
            var names = t
                .GetMembers(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                .Where(m => m is PropertyInfo or FieldInfo)
                .Select(m => m.Name.ToLowerInvariant());

            foreach (string name in names)
            {
                foreach (string forbidden in ForbiddenMemberFragments)
                {
                    Assert.False(name.Contains(forbidden),
                        $"{t.Name} exposes a forbidden token-bearing member '{name}'.");
                }
            }
        }
    }

    [Fact]
    public void NeutralWamResult_DoesNotCarryPresentationOrPhoto()
    {
        var memberTypes = typeof(NeutralWamResult)
            .GetMembers(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .Select(m => m switch
            {
                PropertyInfo p => p.PropertyType,
                FieldInfo f => f.FieldType,
                _ => null,
            })
            .Where(t => t is not null)
            .ToArray();

        Assert.DoesNotContain(typeof(WorkAccountProfilePresentation), memberTypes);
        Assert.DoesNotContain(typeof(WorkAccountPhotoFetchResult), memberTypes);
    }
}
#endif
