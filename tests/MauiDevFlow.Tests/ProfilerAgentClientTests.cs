using System.Net;
using System.Net.Sockets;
using System.Text;

namespace MauiDevFlow.Tests;

public class ProfilerAgentClientTests
{
    [Fact]
    public async Task Profiler_StartStopAndPollFlow_WorksThroughAgentClient()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var serverTask = Task.Run(async () =>
        {
            for (var i = 0; i < 3; i++)
            {
                using var client = await listener.AcceptTcpClientAsync();
                using var stream = client.GetStream();
                var request = await ReadRequestAsync(stream);

                if (request.Contains("POST /api/profiler/start", StringComparison.Ordinal))
                {
                    var body = """
                    {
                      "session": {
                        "sessionId": "s-1",
                        "startedAtUtc": "2026-01-01T00:00:00Z",
                        "sampleIntervalMs": 500,
                        "isActive": true
                      },
                      "capabilities": {
                        "available": true
                      }
                    }
                    """;
                    await WriteJsonResponseAsync(stream, body);
                    continue;
                }

                if (request.Contains("GET /api/profiler/samples", StringComparison.Ordinal))
                {
                    var body = """
                    {
                      "sessionId": "s-1",
                      "samples": [
                        {
                          "tsUtc": "2026-01-01T00:00:00.500Z",
                          "fps": 60.0,
                          "frameTimeMsP50": 16.6,
                          "frameTimeMsP95": 20.1,
                          "worstFrameTimeMs": 48.2,
                          "managedBytes": 2048,
                          "gc0": 1,
                          "gc1": 0,
                          "gc2": 0,
                          "cpuPercent": 12.5,
                          "threadCount": 8,
                          "jankFrameCount": 3,
                          "uiThreadStallCount": 1,
                          "frameSource": "native.android.choreographer",
                          "frameQuality": "estimated"
                        }
                      ],
                      "markers": [
                        {
                          "tsUtc": "2026-01-01T00:00:00.300Z",
                          "type": "navigation.start",
                          "name": "//native"
                        }
                      ],
                      "spans": [
                        {
                          "spanId": "sp-1",
                          "startTsUtc": "2026-01-01T00:00:00.300Z",
                          "endTsUtc": "2026-01-01T00:00:00.340Z",
                          "durationMs": 40.0,
                          "kind": "ui.operation",
                          "name": "action.scroll",
                          "status": "ok",
                          "threadId": 12
                        }
                      ],
                      "sampleCursor": 1,
                      "markerCursor": 1,
                      "spanCursor": 1,
                      "isActive": true
                    }
                    """;
                    await WriteJsonResponseAsync(stream, body);
                    continue;
                }

                if (request.Contains("POST /api/profiler/stop", StringComparison.Ordinal))
                {
                    var body = """
                    {
                      "session": {
                        "sessionId": "s-1",
                        "startedAtUtc": "2026-01-01T00:00:00Z",
                        "sampleIntervalMs": 500,
                        "isActive": false
                      }
                    }
                    """;
                    await WriteJsonResponseAsync(stream, body);
                    continue;
                }

                throw new InvalidOperationException($"Unexpected request: {request}");
            }
        });

        using var client = new MauiDevFlow.Driver.AgentClient("localhost", port);

        var started = await client.StartProfilerAsync(500);
        Assert.NotNull(started);
        Assert.Equal("s-1", started.SessionId);
        Assert.True(started.IsActive);

        var batch = await client.GetProfilerSamplesAsync();
        Assert.NotNull(batch);
        Assert.Equal("s-1", batch.SessionId);
        Assert.Single(batch.Samples);
        Assert.Single(batch.Markers);
        Assert.Single(batch.Spans);
        Assert.Equal("native.android.choreographer", batch.Samples[0].FrameSource);
        Assert.Equal(3, batch.Samples[0].JankFrameCount);
        Assert.Equal(1, batch.SampleCursor);
        Assert.Equal(1, batch.MarkerCursor);
        Assert.Equal(1, batch.SpanCursor);

        var stopped = await client.StopProfilerAsync();
        Assert.NotNull(stopped);
        Assert.False(stopped.IsActive);

        await serverTask;
    }

    private static async Task<string> ReadRequestAsync(NetworkStream stream)
    {
        var buffer = new byte[8192];
        var read = await stream.ReadAsync(buffer);
        return Encoding.UTF8.GetString(buffer, 0, read);
    }

    private static async Task WriteJsonResponseAsync(NetworkStream stream, string body)
    {
        var bodyBytes = Encoding.UTF8.GetBytes(body);
        var headers = $"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {bodyBytes.Length}\r\nConnection: close\r\n\r\n";
        var headerBytes = Encoding.UTF8.GetBytes(headers);
        await stream.WriteAsync(headerBytes);
        await stream.WriteAsync(bodyBytes);
    }
}
