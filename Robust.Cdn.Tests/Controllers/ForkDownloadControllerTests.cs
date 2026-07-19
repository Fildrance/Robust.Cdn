using System.Buffers.Binary;
using System.Net;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.Mvc.Testing;
using Robust.Cdn.Config;
using SharpZstd;

namespace Robust.Cdn.Tests.Controllers;

/// <summary>
/// Integration tests for <c>DownloadController</c>.
///
/// Endpoints:
///   GET  /fork/{fork}/version/{version}/manifest
///   OPTIONS /fork/{fork}/version/{version}/download
///   POST /fork/{fork}/version/{version}/download
/// </summary>
[Trait("Category", "IntegrationTest")]
public sealed class ForkDownloadControllerTests(WebApplicationFactory<Program> factory, DatabaseFixture database)
    : TestBase(factory, database)
{
    protected override string ForkName => "testfork2";

    const string FileContent = "test content";

    protected override Dictionary<string, string?> GetConfigurationOverrides()
    {
        return new()
        {
            ["Manifest:FileDiskPath"] = Database.CreateTestVersionOnDisk(ForkName, "1.0.0", content: FileContent),
        };
    }

    [Theory]
    [InlineData("nonexistent", "1.0.0")]
    [InlineData("nonexistent", "0.0.0")]
    [InlineData("testfork2", "0.0.0")]
    public async Task GetManifest_NonExistentForkOrVersion_ReturnsNotFound(string fork, string version)
    {
        var client = Factory.CreateClient();
        var response = await client.GetAsync($"/fork/{fork}/version/{version}/manifest");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task GetManifest_ExistingVersion_ReturnsOkWithManifestHash()
    {
        var client = Factory.CreateClient();
        var response = await PollUntilOk(() => client.GetAsync($"/fork/{ForkName}/version/1.0.0/manifest"));

        Assert.True(response.Headers.Contains("X-Manifest-Hash"));
        Assert.Equal("text/plain; charset=utf-8", response.Content.Headers.ContentType?.ToString());

        var body = await response.Content.ReadAsStringAsync();
        var lines = body.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Assert.Equal(2, lines.Length);
        Assert.StartsWith("Robust Content Manifest", lines[0]);
        Assert.Equal("8D3BD9BFD005F4179699B9212273284C6DE851AE910EEE2FD1BB0D96EDE65A3D test.txt", lines[1]);
    }

    [Fact]
    public async Task GetManifest_WithZstdEncoding_ReturnsCompressedManifest()
    {
        var client = Factory.CreateClient();
        var response = await PollUntilOk(() =>
        {
            var req = new HttpRequestMessage(HttpMethod.Get, $"/fork/{ForkName}/version/1.0.0/manifest");
            req.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("zstd"));
            return client.SendAsync(req);
        });

        Assert.Equal("zstd", response.Content.Headers.GetValues("Content-Encoding").Single());
        Assert.True(response.Headers.Contains("X-Manifest-Hash"));

        // Decompress the zstd body and verify manifest contents
        await using var bodyStream = await response.Content.ReadAsStreamAsync();
        await using var decompressStream = new ZstdDecodeStream(bodyStream, leaveOpen: false);
        using var reader = new StreamReader(decompressStream);
        var body = await reader.ReadToEndAsync();
        var lines = body.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Assert.Equal(2, lines.Length);
        Assert.StartsWith("Robust Content Manifest", lines[0]);
        Assert.Equal("8D3BD9BFD005F4179699B9212273284C6DE851AE910EEE2FD1BB0D96EDE65A3D test.txt", lines[1]);
    }

    [Fact]
    public async Task DownloadOptions_ReturnsNoContentWithProtocolHeaders()
    {
        var client = Factory.CreateClient();
        var response = await client.SendAsync(new(HttpMethod.Options, $"/fork/{ForkName}/version/1.0.0/download"));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var minProtocol = int.Parse(response.Headers.GetValues("X-Robust-Download-Min-Protocol").Single());
        var maxProtocol = int.Parse(response.Headers.GetValues("X-Robust-Download-Max-Protocol").Single());

        Assert.True(minProtocol <= maxProtocol,
            $"Min protocol ({minProtocol}) must be less than or equal to max protocol ({maxProtocol})");
    }

    [Fact]
    public async Task DownloadPost_WrongContentType_ReturnsBadRequest()
    {
        var client = Factory.CreateClient();
        var content = new StringContent("test");
        var response = await client.PostAsync($"/fork/{ForkName}/version/1.0.0/download", content);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("99")]
    [InlineData("abc")]
    [InlineData("")]
    public async Task DownloadPost_InvalidProtocol_ReturnsBadRequest(string protocol)
    {
        var client = Factory.CreateClient();
        var content = new ByteArrayContent(new byte[4]);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        var request = new HttpRequestMessage(HttpMethod.Post, $"/fork/{ForkName}/version/1.0.0/download")
        {
            Content = content
        };
        request.Headers.Add("X-Robust-Download-Protocol", protocol);
        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task DownloadPost_ValidRequest_ReturnsOkWithStreamContent()
    {
        var client = Factory.CreateClient();

        // Build request body: a single 4-byte LE int with value 0 (index of test.txt in manifest)
        var body = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(body, 0);
        var content = new ByteArrayContent(body);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");

        var response = await PollUntilOk(() =>
        {
            var request = new HttpRequestMessage(HttpMethod.Post, $"/fork/{ForkName}/version/1.0.0/download")
            {
                Content = content
            };
            request.Headers.Add("X-Robust-Download-Protocol", "1");
            return client.SendAsync(request);
        });

        // Read the response body and parse the download stream format
        var responseBody = await response.Content.ReadAsByteArrayAsync();
        Assert.True(responseBody.Length >= 4, "Response must contain at least the stream header");

        // Next 4 bytes: file header (uncompressed size)
        var expectedSize = System.Text.Encoding.UTF8.GetByteCount(FileContent);
        var fileSize = BinaryPrimitives.ReadInt32LittleEndian(responseBody.AsSpan(4, 4));
        Assert.Equal(expectedSize, fileSize);

        // Rest of body: file data
        var fileData = System.Text.Encoding.UTF8.GetString(responseBody, 8, fileSize);
        Assert.Equal(FileContent, fileData);
    }

    [Fact]
    public async Task DownloadPost_WithZstdStreamCompression_ReturnsCompressedStream()
    {
        var client = Factory.CreateClient();
        var body = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(body, 0);

        // Since the default AutoStreamCompressRatio is 0.5 and we request 1 of 1 file,
        // the ratio 1.0 > 0.5 triggers stream compression automatically.
        // We need Accept-Encoding: zstd for the server to actually use it.
        var response = await PollUntilOk(() =>
        {
            var request = new HttpRequestMessage(HttpMethod.Post, $"/fork/{ForkName}/version/1.0.0/download")
            {
                Content = new ByteArrayContent(body)
                {
                    Headers = { ContentType = new MediaTypeHeaderValue("application/octet-stream") }
                }
            };
            request.Headers.Add("X-Robust-Download-Protocol", "1");
            request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("zstd"));
            return client.SendAsync(request);
        });

        // Verify the response is zstd-compressed at the stream level
        Assert.Equal("zstd", response.Content.Headers.GetValues("Content-Encoding").Single());

        // Decompress the entire response body
        var decompressedBody = await DecompressBody(response);

        Assert.True(decompressedBody.Length >= 8, "Decompressed stream must contain header + file header");

        // Next 4 bytes: file header (uncompressed size)
        var expectedSize = System.Text.Encoding.UTF8.GetByteCount(FileContent);
        var fileSize = BinaryPrimitives.ReadInt32LittleEndian(decompressedBody.AsSpan(4, 4));
        Assert.Equal(expectedSize, fileSize);

        // Rest: file data
        var fileData = decompressedBody[8..];
        var fileContent = System.Text.Encoding.UTF8.GetString(fileData);
        Assert.Equal(FileContent, fileContent);
    }

    [Fact]
    public async Task DownloadPost_WithAutoRatioOptingForPreCompression_ReturnsPreCompressedStream()
    {
        var body = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(body, 0);

        // Use default AutoStreamCompressRatio (0.5) and request 1 file out of 1 distinct blob.
        // The ratio 1.0 > 0.5 would go to stream compression branch, so we set ratio high (e.g. 2.0)
        // so 1.0 <= 2.0 triggers the else branch: pre-compression.
        var factory = Factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, configBuilder) =>
            {
                configBuilder.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    [$"Cdn:{nameof(CdnOptions.AutoStreamCompressRatio)}"] = "2.0",
                });
            });
        });

        var client = factory.CreateClient();
        var response = await PollUntilOk(() =>
        {
            var request = new HttpRequestMessage(HttpMethod.Post, $"/fork/{ForkName}/version/1.0.0/download")
            {
                Content = new ByteArrayContent(body)
                {
                    Headers = { ContentType = new MediaTypeHeaderValue("application/octet-stream") }
                }
            };
            request.Headers.Add("X-Robust-Download-Protocol", "1");
            return client.SendAsync(request);
        });

        // No stream-level compression since else branch sets optStreamCompression = false.
        // Pre-compression is active, so response body is raw with 8-byte per-file headers.
        var responseBody = await response.Content.ReadAsByteArrayAsync();

        // Stream header (4 bytes)
        Assert.True(responseBody.Length >= 4, "Response must contain stream header");
        var streamHeaderFlags = BinaryPrimitives.ReadInt32LittleEndian(responseBody.AsSpan(0, 4));
        // StreamHeaderFlags.PreCompressed = 1, so flags should be non-zero
        Assert.Equal(1, streamHeaderFlags);

        // File header: 8 bytes (4 size + 4 compression info) since pre-compressed
        Assert.True(responseBody.Length >= 12, "Response must contain full file header");
        var expectedSize = System.Text.Encoding.UTF8.GetByteCount(FileContent);
        var fileSize = BinaryPrimitives.ReadInt32LittleEndian(responseBody.AsSpan(4, 4));
        Assert.Equal(expectedSize, fileSize);
        var compInfo = BinaryPrimitives.ReadInt32LittleEndian(responseBody.AsSpan(8, 4));
        Assert.Equal(0, compInfo); // No compression for this test data

        // File data
        var fileContent = System.Text.Encoding.UTF8.GetString(responseBody[12..]);
        Assert.Equal(FileContent, fileContent);
    }

    [Fact]
    public async Task DownloadPost_WithAutoStreamCompressRatioDisabled_StreamCompressFallback()
    {
        var factory = Factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, configBuilder) =>
            {
                configBuilder.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    [$"Cdn:{nameof(CdnOptions.AutoStreamCompressRatio)}"] = "-1",
                    [$"Cdn:{nameof(CdnOptions.StreamCompress)}"] = "true",
                    [$"Cdn:{nameof(CdnOptions.SendPreCompressed)}"] = "false",
                });
            });
        });

        var client = factory.CreateClient();
        var body = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(body, 0);

        var response = await PollUntilOk(() =>
        {
            var request = new HttpRequestMessage(HttpMethod.Post, $"/fork/{ForkName}/version/1.0.0/download")
            {
                Content = new ByteArrayContent(body)
                {
                    Headers = { ContentType = new MediaTypeHeaderValue("application/octet-stream") }
                }
            };
            request.Headers.Add("X-Robust-Download-Protocol", "1");
            request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("zstd"));
            return client.SendAsync(request);
        });

        Assert.Equal("zstd", response.Content.Headers.GetValues("Content-Encoding").Single());

        var decompressedBody = await DecompressBody(response);

        Assert.True(decompressedBody.Length >= 8, "Decompressed stream must contain header + file header");
        var expectedSize = System.Text.Encoding.UTF8.GetByteCount(FileContent);
        var fileSize = BinaryPrimitives.ReadInt32LittleEndian(decompressedBody.AsSpan(4, 4));
        Assert.Equal(expectedSize, fileSize);

        var fileData = decompressedBody[8..];
        var fileContent = System.Text.Encoding.UTF8.GetString(fileData);
        Assert.Equal(FileContent, fileContent);
    }
}

