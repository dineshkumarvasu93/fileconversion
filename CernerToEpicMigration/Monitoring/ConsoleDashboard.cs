using System.Text;
using CernerToEpicMigration.Configuration;

namespace CernerToEpicMigration.Monitoring;

/// <summary>
/// Live console dashboard from design document section 11.1. Rendering happens on a
/// single background task, so worker threads never write to the console themselves.
/// </summary>
public sealed class ConsoleDashboard
{
    private const int InnerWidth = 68;
    private const int LeftCellWidth = 36;
    private const int RightCellWidth = 31;

    private readonly MetricsCollector _metrics;
    private readonly DashboardOptions _options;
    private readonly object _renderLock = new();
    private bool _canRedraw;

    private Task? _renderLoop;
    private int _originTop;
    private int _renderedLines;
    private string _status = "RUNNING";

    public ConsoleDashboard(MetricsCollector metrics, DashboardOptions options)
    {
        _metrics = metrics;
        _options = options;
        _canRedraw = !Console.IsOutputRedirected;
    }

    public void Start(CancellationToken cancellationToken)
    {
        if (!_options.EnableConsoleDashboard)
            return;

        _renderLoop = Task.Run(async () =>
        {
            TimeSpan interval = TimeSpan.FromSeconds(_options.RefreshIntervalSeconds);

            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    Render();
                    await Task.Delay(interval, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                // Shutting down - the final frame is drawn by StopAsync.
            }
        }, CancellationToken.None);
    }

    /// <summary>Stops the refresh loop and draws one last frame with the final status.</summary>
    public async Task StopAsync(string status)
    {
        _status = status;

        if (_renderLoop is not null)
        {
            await _renderLoop.ConfigureAwait(false);
            _renderLoop = null;
        }

        if (_options.EnableConsoleDashboard)
        {
            Render();
            Console.WriteLine();
        }
    }

    private void Render()
    {
        string frame = BuildFrame();

        if (!_canRedraw)
        {
            // Redirected output (log file, CI): one compact line per refresh instead
            // of repainting a box that nobody can see.
            Console.WriteLine(
                $"[{_metrics.Elapsed:hh\\:mm\\:ss}] {_status} " +
                $"processed={_metrics.Processed:N0}/{_metrics.TotalFound:N0} " +
                $"ok={_metrics.Succeeded:N0} failed={_metrics.Failed:N0} " +
                $"rate={_metrics.FilesPerSecond:N1}/s folder={_metrics.CurrentFolder}");
            return;
        }

        string[] lines = frame.Split(Environment.NewLine);

        lock (_renderLock)
        {
            try
            {
                if (_renderedLines == 0)
                {
                    // Reserve the rows first so the origin stays valid even if the
                    // console scrolls while the block is being written.
                    for (int i = 0; i < lines.Length; i++)
                        Console.WriteLine();

                    _originTop = Math.Max(0, Console.CursorTop - lines.Length);
                    _renderedLines = lines.Length;
                }

                Console.SetCursorPosition(0, _originTop);
                foreach (string line in lines)
                    Console.WriteLine(line.PadRight(Math.Max(0, Console.WindowWidth - 1)));
            }
            catch (Exception exception) when (exception is IOException or ArgumentOutOfRangeException)
            {
                // Console too small, resized away, or no cursor control available:
                // drop to the single-line form rather than losing progress output.
                _canRedraw = false;
            }
        }
    }

    private string BuildFrame()
    {
        long processed = _metrics.Processed;
        long total = _metrics.TotalFound;
        double percent = total == 0 ? 0 : processed * 100d / total;
        double filesPerSecond = _metrics.FilesPerSecond;
        TimeSpan? remaining = _metrics.EstimatedRemaining;

        StringBuilder frame = new();
        frame.AppendLine(Border('='));
        frame.AppendLine(Centered("CERNER TO EPIC MIGRATION - STAGE 1 (XHTML -> RTF)"));
        frame.AppendLine(Border('='));
        frame.AppendLine(Row(
            Cell("Status:", _status, LeftCellWidth),
            Cell("Elapsed:", $"{_metrics.Elapsed:hh\\:mm\\:ss}", RightCellWidth)));
        frame.AppendLine(Separator());
        frame.AppendLine(Row(
            Cell("Total Files Found:", total.ToString("N0"), LeftCellWidth),
            Cell("Current Batch:", $"{_metrics.CurrentBatch:N0} / {_metrics.TotalBatches:N0}", RightCellWidth)));
        frame.AppendLine(Row(
            Cell("Files Processed:", processed.ToString("N0"), LeftCellWidth),
            Cell("Active Threads:", _metrics.ActiveWorkers.ToString("N0"), RightCellWidth)));
        frame.AppendLine(Row(
            Cell("Files Succeeded:", _metrics.Succeeded.ToString("N0"), LeftCellWidth),
            Cell("Files/Second:", filesPerSecond.ToString("N1"), RightCellWidth)));
        frame.AppendLine(Row(
            Cell("Files Failed:", _metrics.Failed.ToString("N0"), LeftCellWidth),
            Cell("Files/Minute:", (filesPerSecond * 60).ToString("N0"), RightCellWidth)));
        frame.AppendLine(Row(
            Cell("Files Remaining:", _metrics.Remaining.ToString("N0"), LeftCellWidth),
            Cell("Files/Hour:", (filesPerSecond * 3600).ToString("N0"), RightCellWidth)));
        frame.AppendLine(Separator());
        frame.AppendLine(Single($" Progress: [{ProgressBar(percent)}] {percent,5:N1}%"));
        frame.AppendLine(Separator());
        frame.AppendLine(Row(
            Cell("Est. Remaining:", remaining is null ? "--:--:--" : $"{remaining:hh\\:mm\\:ss}", LeftCellWidth),
            Cell("CPU Usage:", $"{_metrics.SampleCpuPercent():N0}%", RightCellWidth)));
        frame.AppendLine(Row(
            Cell("Memory Usage:", $"{_metrics.MemoryBytes / (1024d * 1024d * 1024d):N2} GB", LeftCellWidth),
            Cell("File I/O:", $"{_metrics.MegabytesPerSecond:N0} MB/s", RightCellWidth)));
        frame.AppendLine(Border('='));
        frame.AppendLine(Row(
            Cell("Current Folder:", Truncate(_metrics.CurrentFolder, 14), LeftCellWidth),
            Cell("Errors This Batch:", _metrics.ErrorsInCurrentBatch.ToString("N0"), RightCellWidth)));
        frame.Append(Border('='));

        return frame.ToString();
    }

    private static string Border(char fill) => "+" + new string(fill, InnerWidth) + "+";

    private static string Separator() =>
        "+" + new string('-', LeftCellWidth) + "+" + new string('-', RightCellWidth) + "+";

    private static string Row(string left, string right) => "|" + left + "|" + right + "|";

    private static string Single(string content) => "|" + Fit(content, InnerWidth) + "|";

    private static string Centered(string text)
    {
        int padding = Math.Max(0, (InnerWidth - text.Length) / 2);
        return "|" + Fit(new string(' ', padding) + text, InnerWidth) + "|";
    }

    private static string Cell(string label, string value, int width)
    {
        int valueWidth = Math.Max(1, width - label.Length - 3);
        return Fit($" {label} {value.PadLeft(valueWidth)} ", width);
    }

    private static string Fit(string text, int width) =>
        text.Length > width ? text[..width] : text.PadRight(width);

    private static string Truncate(string text, int maxLength) =>
        text.Length <= maxLength ? text : text[..(maxLength - 1)] + "~";

    private static string ProgressBar(double percent)
    {
        const int barWidth = 42;
        int filled = (int)Math.Round(percent / 100d * barWidth, MidpointRounding.AwayFromZero);
        filled = Math.Clamp(filled, 0, barWidth);

        if (filled == 0)
            return new string(' ', barWidth);

        return new string('=', filled - 1) + ">" + new string(' ', barWidth - filled);
    }
}
