using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using CernerToEpicMigration.Processing;

namespace CernerToEpicMigration.Tests;

/// <summary>
/// A converter double that records concurrency and call order and fails to a script, so the
/// threading, retry and batching behaviour of the pipeline can be asserted exactly.
/// </summary>
/// <remarks>
/// The real Telerik converter cannot be made to fail transiently on demand, and it cannot report
/// which worker called it. Both are needed to test the settings that govern a bulk run, so those
/// tests use this instead. The end-to-end tests still use the real converter.
/// </remarks>
internal sealed class InstrumentedConverter : IXhtmlToRtfConverter
{
    /// <summary>Maps (file, 1-based attempt number) to the exception to throw, or null to succeed.</summary>
    private readonly Func<string, int, Exception?>? _failure;

    private readonly TimeSpan _work;
    private readonly ConcurrentDictionary<string, int> _attempts = new();

    private int _active;
    private int _peakConcurrency;
    private int _totalCalls;

    public InstrumentedConverter(Func<string, int, Exception?>? failure = null, TimeSpan? work = null)
    {
        _failure = failure;
        _work = work ?? TimeSpan.FromMilliseconds(5);
    }

    /// <summary>Most calls that were ever in flight at once.</summary>
    public int PeakConcurrency => Volatile.Read(ref _peakConcurrency);

    /// <summary>Every call, including retries.</summary>
    public int TotalCalls => Volatile.Read(ref _totalCalls);

    /// <summary>One entry per call: when it started and finished, on the stopwatch timeline.</summary>
    public ConcurrentQueue<(string File, long StartTicks, long EndTicks)> Calls { get; } = new();

    /// <summary>How many times a given document was handed to the converter.</summary>
    public int AttemptsFor(string filePath) => _attempts.TryGetValue(filePath, out int count) ? count : 0;

    public ConversionOutcome Convert(string inputPath, string outputPath)
    {
        RecordPeak(Interlocked.Increment(ref _active));
        long start = Stopwatch.GetTimestamp();

        try
        {
            Interlocked.Increment(ref _totalCalls);
            int attempt = _attempts.AddOrUpdate(inputPath, 1, (_, previous) => previous + 1);

            // Held long enough for workers to overlap; without it the calls can finish faster
            // than the scheduler starts them and the peak reads as 1 on a correct run.
            Thread.Sleep(_work);

            if (_failure?.Invoke(inputPath, attempt) is { } error)
                throw error;

            // A real RTF so FileManager, the archive path and the output assertions behave
            // exactly as they do with the real converter.
            const string rtf = @"{\rtf1\ansi\deff0{\fonttbl{\f0 Segoe UI;}}\f0 test\par}";
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
            File.WriteAllText(outputPath, rtf, Encoding.ASCII);

            return new ConversionOutcome(new FileInfo(inputPath).Length, rtf.Length);
        }
        finally
        {
            Interlocked.Decrement(ref _active);
            Calls.Enqueue((inputPath, start, Stopwatch.GetTimestamp()));
        }
    }

    private void RecordPeak(int active)
    {
        int peak = Volatile.Read(ref _peakConcurrency);

        while (active > peak)
        {
            int seen = Interlocked.CompareExchange(ref _peakConcurrency, active, peak);
            if (seen == peak)
                return;

            peak = seen;
        }
    }
}
