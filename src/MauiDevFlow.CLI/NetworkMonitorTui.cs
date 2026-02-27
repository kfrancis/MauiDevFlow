using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using XenoAtom.Terminal;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Controls;
using XenoAtom.Terminal.UI.DataGrid;
using XenoAtom.Terminal.UI.Geometry;
using XenoAtom.Terminal.UI.Styling;

namespace MauiDevFlow.CLI;

/// <summary>
/// Bindable model for a network request row in the DataGrid.
/// </summary>
public sealed partial class NetworkRow
{
    [Bindable] public partial int Index { get; set; }
    [Bindable] public partial string Id { get; set; }
    [Bindable] public partial string Method { get; set; }
    [Bindable] public partial string Url { get; set; }
    [Bindable] public partial string Host { get; set; }
    [Bindable] public partial int StatusCode { get; set; }
    [Bindable] public partial string StatusText { get; set; }
    [Bindable] public partial string Duration { get; set; }
    [Bindable] public partial string Size { get; set; }
    [Bindable] public partial string Error { get; set; }
    [Bindable] public partial string ContentType { get; set; }
    [Bindable] public partial string Timestamp { get; set; }

    public NetworkRow()
    {
        Id = string.Empty;
        Method = string.Empty;
        Url = string.Empty;
        Host = string.Empty;
        StatusText = string.Empty;
        Duration = string.Empty;
        Size = string.Empty;
        Error = string.Empty;
        ContentType = string.Empty;
        Timestamp = string.Empty;
    }
}

/// <summary>
/// Full-screen TUI for monitoring network requests using XenoAtom.Terminal.UI.
/// </summary>
public static class NetworkMonitorTui
{
    public static async Task RunAsync(string host, int port, string? filterHost, string? filterMethod)
    {
        var wsUrl = $"ws://{host}:{port}/ws/network";
        var exit = new State<bool>(false);
        var status = new State<string>("Connecting...");
        var detailText = new State<string>("");
        var showDetail = new State<bool>(false);
        var selectedId = new State<string?>("");
        var requestCount = new State<int>(0);

        // Track rows for detail lookups
        var rows = new List<NetworkRow>();

        // DataGrid document
        var doc = new DataGridListDocument<NetworkRow>();
        using (doc.BeginUpdate())
        {
            doc.AddColumn(new DataGridColumnInfo<int>("index", "#", ReadOnly: true, NetworkRow.Accessor.Index));
            doc.AddColumn(new DataGridColumnInfo<string>("method", "Method", ReadOnly: true, NetworkRow.Accessor.Method));
            doc.AddColumn(new DataGridColumnInfo<string>("url", "URL", ReadOnly: true, NetworkRow.Accessor.Url));
            doc.AddColumn(new DataGridColumnInfo<int>("statusCode", "Status", ReadOnly: true, NetworkRow.Accessor.StatusCode));
            doc.AddColumn(new DataGridColumnInfo<string>("duration", "Duration", ReadOnly: true, NetworkRow.Accessor.Duration));
            doc.AddColumn(new DataGridColumnInfo<string>("size", "Size", ReadOnly: true, NetworkRow.Accessor.Size));
            doc.AddColumn(new DataGridColumnInfo<string>("contentType", "Content-Type", ReadOnly: true, NetworkRow.Accessor.ContentType));
        }

        using var view = new DataGridDocumentView(doc);

        var grid = new DataGridControl { View = view }
            .ShowHeader(true)
            .ShowRowAnchor(true)
            .RowAnchorWidth(1)
            .ReadOnly(true)
            .SelectionMode(DataGridSelectionMode.Row)
            .FrozenColumns(0)
            .HorizontalAlignment(Align.Stretch)
            .VerticalAlignment(Align.Stretch);

        grid.Columns.Add(new DataGridColumn<int>
        {
            Key = "index",
            TypedValueAccessor = NetworkRow.Accessor.Index,
            Width = GridLength.Fixed(5),
            CellAlignment = TextAlignment.Right,
        });
        grid.Columns.Add(new DataGridColumn<string>
        {
            Key = "method",
            TypedValueAccessor = NetworkRow.Accessor.Method,
            Width = GridLength.Fixed(7),
        });
        grid.Columns.Add(new DataGridColumn<string>
        {
            Key = "url",
            TypedValueAccessor = NetworkRow.Accessor.Url,
            Width = GridLength.Star(1),
        });
        grid.Columns.Add(new DataGridColumn<int>
        {
            Key = "statusCode",
            TypedValueAccessor = NetworkRow.Accessor.StatusCode,
            Width = GridLength.Fixed(7),
            CellAlignment = TextAlignment.Right,
        });
        grid.Columns.Add(new DataGridColumn<string>
        {
            Key = "duration",
            TypedValueAccessor = NetworkRow.Accessor.Duration,
            Width = GridLength.Fixed(10),
            CellAlignment = TextAlignment.Right,
        });
        grid.Columns.Add(new DataGridColumn<string>
        {
            Key = "size",
            TypedValueAccessor = NetworkRow.Accessor.Size,
            Width = GridLength.Fixed(10),
            CellAlignment = TextAlignment.Right,
        });
        grid.Columns.Add(new DataGridColumn<string>
        {
            Key = "contentType",
            TypedValueAccessor = NetworkRow.Accessor.ContentType,
            Width = GridLength.Fixed(20),
        });

        // Detail view
        var detailArea = new TextArea(detailText)
            .HorizontalAlignment(Align.Stretch)
            .VerticalAlignment(Align.Stretch);

        // Header
        var header = new HStack(
            new TextBlock(() => $"🌐 Network Monitor — {host}:{port}") { Wrap = false },
            new TextBlock(() => $"  [{requestCount.Value} requests]") { Wrap = false }
        ).Spacing(0).HorizontalAlignment(Align.Stretch);

        // Footer with key hints
        var footer = new Footer
        {
            Left = new TextBlock(() => status.Value),
            Right = new Markup("[dim]Enter[/]: Details  [dim]Esc[/]: Back  [dim]Ctrl+Q[/]: Quit") { Wrap = false },
        };

        // Main layout: grid view and detail view, swapped via showDetail state
        var mainContent = new ComputedVisual(() =>
        {
            if (showDetail.Value)
            {
                return new VStack(
                    new HStack(
                        new Markup("[bold]Request Details[/]") { Wrap = false },
                        new TextBlock(() => $"  {selectedId.Value}") { Wrap = false }
                    ).Spacing(0),
                    new Rule(),
                    new ScrollViewer(detailArea)
                        .HorizontalAlignment(Align.Stretch)
                        .VerticalAlignment(Align.Stretch)
                ).Spacing(0).HorizontalAlignment(Align.Stretch).VerticalAlignment(Align.Stretch);
            }

            return new ScrollViewer(grid)
                .HorizontalAlignment(Align.Stretch)
                .VerticalAlignment(Align.Stretch);
        });

        var root = new DockLayout()
            .HorizontalAlignment(Align.Stretch)
            .VerticalAlignment(Align.Stretch)
            .Top(new VStack(header, new Rule()).Spacing(0))
            .Content(mainContent)
            .Bottom(footer);

        // Handle Enter key on grid to show details
        grid.KeyDown((_, e) =>
        {
            if (e.Key == TerminalKey.Enter)
            {
                _ = LoadDetailAsync(host, port, rows, grid, selectedId, detailText, showDetail, status);
                e.Handled = true;
            }
        });

        // Handle Escape to go back from detail view - attach to root
        root.KeyDown((_, e) =>
        {
            if (e.Key == TerminalKey.Escape && showDetail.Value)
            {
                showDetail.Value = false;
                status.Value = "Listening...";
                e.Handled = true;
            }
        });

        // Start WebSocket connection in background
        var cts = new CancellationTokenSource();
        var wsTask = Task.Run(() => WebSocketLoop(wsUrl, doc, rows, filterHost, filterMethod, requestCount, status, exit, cts.Token));

        using var session = Terminal.Open();

        Terminal.Run(root, ctx =>
        {
            if (exit.Value) return TerminalLoopResult.StopAndKeepVisual;
            return TerminalLoopResult.Continue;
        }, new TerminalRunOptions { UpdateWaitDuration = TimeSpan.FromMilliseconds(16) });

        cts.Cancel();
        try { await wsTask; } catch { }
    }

    private static async Task LoadDetailAsync(
        string host, int port,
        List<NetworkRow> rows,
        DataGridControl grid,
        State<string?> selectedId,
        State<string> detailText,
        State<bool> showDetail,
        State<string> status)
    {
        var currentRow = grid.CurrentCell.Row;
        if (currentRow < 0 || currentRow >= rows.Count) return;

        var row = rows[currentRow];
        var id = row.Id;
        selectedId.Value = id;
        status.Value = $"Loading details for {id}...";

        try
        {
            using var client = new MauiDevFlow.Driver.AgentClient(host, port);
            var detail = await client.GetNetworkRequestDetailAsync(id);
            if (detail == null)
            {
                detailText.Value = "Request not found.";
                showDetail.Value = true;
                return;
            }

            var sb = new StringBuilder();
            sb.AppendLine($"ID:          {detail.Id}");
            sb.AppendLine($"Timestamp:   {detail.Timestamp}");
            sb.AppendLine($"Method:      {detail.Method}");
            sb.AppendLine($"URL:         {detail.Url}");

            if (!string.IsNullOrEmpty(detail.Error))
            {
                sb.AppendLine($"Error:       {detail.Error}");
            }
            else
            {
                sb.AppendLine($"Status:      {detail.StatusCode} {detail.StatusText}");
            }

            sb.AppendLine($"Duration:    {detail.DurationMs}ms");
            sb.AppendLine();

            // Request headers
            if (detail.RequestHeaders is { Count: > 0 })
            {
                sb.AppendLine("── Request Headers ──");
                foreach (var h in detail.RequestHeaders)
                    sb.AppendLine($"  {h.Key}: {string.Join(", ", h.Value)}");
                sb.AppendLine();
            }

            // Request body
            if (!string.IsNullOrEmpty(detail.RequestBody))
            {
                sb.AppendLine($"── Request Body ({detail.RequestSize} bytes){(detail.RequestBodyTruncated ? " [truncated]" : "")} ──");
                sb.AppendLine(TryFormatJson(detail.RequestBody));
                sb.AppendLine();
            }

            // Response headers
            if (detail.ResponseHeaders is { Count: > 0 })
            {
                sb.AppendLine("── Response Headers ──");
                foreach (var h in detail.ResponseHeaders)
                    sb.AppendLine($"  {h.Key}: {string.Join(", ", h.Value)}");
                sb.AppendLine();
            }

            // Response body
            if (!string.IsNullOrEmpty(detail.ResponseBody))
            {
                sb.AppendLine($"── Response Body ({detail.ResponseSize} bytes){(detail.ResponseBodyTruncated ? " [truncated]" : "")} ──");
                sb.AppendLine(TryFormatJson(detail.ResponseBody));
            }

            detailText.Value = sb.ToString();
            showDetail.Value = true;
            status.Value = $"Viewing: {detail.Method} {detail.Url}";
        }
        catch (Exception ex)
        {
            detailText.Value = $"Error loading details: {ex.Message}";
            showDetail.Value = true;
        }
    }

    private static string TryFormatJson(string text)
    {
        try
        {
            using var doc = JsonDocument.Parse(text);
            return JsonSerializer.Serialize(doc, new JsonSerializerOptions { WriteIndented = true });
        }
        catch { return text; }
    }

    private static async Task WebSocketLoop(
        string wsUrl,
        DataGridListDocument<NetworkRow> doc,
        List<NetworkRow> rows,
        string? filterHost,
        string? filterMethod,
        State<int> requestCount,
        State<string> status,
        State<bool> exit,
        CancellationToken ct)
    {
        int counter = 0;

        while (!ct.IsCancellationRequested && !exit.Value)
        {
            try
            {
                using var ws = new ClientWebSocket();
                await ws.ConnectAsync(new Uri(wsUrl), ct);
                status.Value = "Listening...";

                var buffer = new byte[65536];
                var sb = new StringBuilder();

                while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
                {
                    WebSocketReceiveResult result;
                    sb.Clear();
                    do
                    {
                        result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
                        if (result.MessageType == WebSocketMessageType.Close) break;
                        sb.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
                    } while (!result.EndOfMessage);

                    if (result.MessageType == WebSocketMessageType.Close) break;
                    if (ct.IsCancellationRequested) break;

                    var msg = sb.ToString();
                    if (string.IsNullOrEmpty(msg)) continue;

                    try
                    {
                        using var jsonDoc = JsonDocument.Parse(msg);
                        var type = jsonDoc.RootElement.GetProperty("type").GetString();

                        if (type == "replay" && jsonDoc.RootElement.TryGetProperty("entries", out var entries))
                        {
                            foreach (var entry in entries.EnumerateArray())
                            {
                                if (!MatchesFilter(entry, filterHost, filterMethod)) continue;
                                counter++;
                                var row = CreateRow(counter, entry);
                                doc.AddRow(row);
                                rows.Add(row);
                                requestCount.Value = counter;
                            }
                        }
                        else if (type == "request" && jsonDoc.RootElement.TryGetProperty("entry", out var reqEntry))
                        {
                            if (!MatchesFilter(reqEntry, filterHost, filterMethod)) continue;
                            counter++;
                            var row = CreateRow(counter, reqEntry);
                            doc.InsertRow(0, row);
                            rows.Insert(0, row);
                            // Re-number rows
                            for (int i = 0; i < rows.Count; i++)
                                rows[i].Index = rows.Count - i;
                            requestCount.Value = counter;
                        }
                    }
                    catch (JsonException) { }
                }
            }
            catch (OperationCanceledException) { break; }
            catch (WebSocketException)
            {
                if (ct.IsCancellationRequested) break;
                status.Value = "Reconnecting...";
                try { await Task.Delay(1000, ct); } catch { break; }
            }
            catch (Exception ex)
            {
                if (ct.IsCancellationRequested) break;
                status.Value = $"Error: {ex.Message}";
                try { await Task.Delay(2000, ct); } catch { break; }
            }
        }
    }

    private static NetworkRow CreateRow(int index, JsonElement entry)
    {
        var statusCode = entry.TryGetProperty("statusCode", out var sc) && sc.ValueKind == JsonValueKind.Number ? sc.GetInt32() : 0;
        var durationMs = entry.TryGetProperty("durationMs", out var dm) && dm.ValueKind == JsonValueKind.Number ? dm.GetInt64() : 0;
        var responseSize = entry.TryGetProperty("responseSize", out var rs) && rs.ValueKind == JsonValueKind.Number ? rs.GetInt64() : 0;
        var error = entry.TryGetProperty("error", out var er) && er.ValueKind == JsonValueKind.String ? er.GetString() ?? "" : "";

        string statusText;
        if (!string.IsNullOrEmpty(error))
            statusText = "ERR";
        else if (statusCode > 0)
            statusText = statusCode.ToString();
        else
            statusText = "---";

        return new NetworkRow
        {
            Index = index,
            Id = entry.TryGetProperty("id", out var id) ? id.GetString() ?? "" : "",
            Method = entry.TryGetProperty("method", out var m) ? m.GetString() ?? "" : "",
            Url = entry.TryGetProperty("url", out var u) ? u.GetString() ?? "" : "",
            Host = entry.TryGetProperty("host", out var h) ? h.GetString() ?? "" : "",
            StatusCode = statusCode,
            StatusText = statusText,
            Duration = $"{durationMs}ms",
            Size = FormatSize(responseSize),
            Error = error,
            ContentType = entry.TryGetProperty("responseContentType", out var ct) ? ct.GetString() ?? "" : "",
            Timestamp = entry.TryGetProperty("timestamp", out var ts) ? ts.GetString() ?? "" : "",
        };
    }

    private static string FormatSize(long bytes)
    {
        if (bytes <= 0) return "--";
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        return $"{bytes / (1024.0 * 1024.0):F1} MB";
    }

    private static bool MatchesFilter(JsonElement entry, string? filterHost, string? filterMethod)
    {
        if (filterHost != null)
        {
            var h = entry.TryGetProperty("host", out var hv) ? hv.GetString() : null;
            if (h == null || !h.Contains(filterHost, StringComparison.OrdinalIgnoreCase)) return false;
        }
        if (filterMethod != null)
        {
            var m = entry.TryGetProperty("method", out var mv) ? mv.GetString() : null;
            if (!string.Equals(m, filterMethod, StringComparison.OrdinalIgnoreCase)) return false;
        }
        return true;
    }
}
