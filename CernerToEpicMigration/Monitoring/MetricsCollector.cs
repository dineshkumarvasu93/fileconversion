using System.Collections.Concurrent;
using System.Diagnostics;
using CernerToEpicMigration.Models;

namespace CernerToEpicMigration.Monitoring;

/// <summary>
/// Thread-safe counters shared by all worker threads (design document section 9.3).
/// All mutation goes through <see cref="Interlocked"/>; nothing here takes a lock.
/// </summary>
public sealed class MetricsCollector
{
    private readonly Stopwatch _stopwatch = new();
    private readonly ConcurrentDictionary<string, FolderStatistics> _folders = new();
    private readonly Process _process = Process.GetCurrentProcess();

    /// <summary>
    /// Recycled worker-slot ids. A slot is held for the whole of one file, so it survives the
    /// <c>await</c> in the retry path; a managed thread id does not, because the thread pool is
    /// free to resume the continuation somewhere else. That makes a slot the only worker identity
    /// worth putting in a trace.
    /// </summary>
    private readonly ConcurrentBag<int> _freeSlots = new();

    private long _totalFound;
    private long _succeeded;
    private long _failed;
    private long _retries;
    private long _bytesRead;
    private long _bytesWritten;
    private long _workerTicks;
    private int _currentBatch;
    private int _totalBatches;
    private int _errorsInCurrentBatch;
    private int _peakActiveWorkers;
    private int _slotsHandedOut;
    private volatile int _activeWorkers;
    private volatile string _currentFolder = "-";

    private TimeSpan _lastCpuTime;
    private TimeSpan _lastCpuSampleAt;
    private double _cpuPercent;

    /// <summary>Counts carried over from a previous run when --resume is used.</summary>
    public long BaselineSucceeded { get; set; }

    public long BaselineFailed { get; set; }

    public long TotalFound => Interlocked.Read(ref _totalFound);

    public long Succeeded => Interlocked.Read(ref _succeeded);

    public long Failed => Interlocked.Read(ref _failed);

    public long Retries => Interlocked.Read(ref _retries);

    public long Processed => Succeeded + Failed;

    public long Remaining => Math.Max(0, TotalFound - Processed);

    public long BytesRead => Interlocked.Read(ref _bytesRead);

    public long BytesWritten => Interlocked.Read(ref _bytesWritten);

    public string CurrentFolder => _currentFolder;

    public int CurrentBatch => _currentBatch;

    public int TotalBatches => _totalBatches;

    public int ActiveWorkers => _activeWorkers;

    /// <summary>
    /// The most workers that were ever in flight at once. This is the check on
    /// <c>MaxDegreeOfParallelism</c>: it must never exceed the configured value, and on a run of
    /// any size it should reach it - a peak of 1 means something serialised the run.
    /// </summary>
    public int PeakActiveWorkers => Volatile.Read(ref _peakActiveWorkers);

    /// <summary>
    /// Worker-time divided by wall-clock time: how many workers were busy on average across the
    /// run. <see cref="PeakActiveWorkers"/> only proves the configured number of workers
    /// overlapped once; this proves they stayed occupied. Well short of the configured
    /// parallelism means the workers are starved - blocked on I/O, or idling at a batch boundary
    /// while one straggler finishes.
    /// </summary>
    public double AverageConcurrency
    {
        get
        {
            long elapsedTicks = _stopwatch.Elapsed.Ticks;
            return elapsedTicks <= 0 ? 0 : (double)Interlocked.Read(ref _workerTicks) / elapsedTicks;
        }
    }

    public int ErrorsInCurrentBatch => _errorsInCurrentBatch;

    public TimeSpan Elapsed => _stopwatch.Elapsed;

    /// <summary>Snapshot of the per-folder counters, ordered by folder name.</summary>
    public IReadOnlyList<FolderStatistics> GetFolderStatistics() =>
        _folders.Values.OrderBy(folder => folder.Name, StringComparer.Ordinal).ToList();

    public double FilesPerSecond
    {
        get
        {
            double seconds = _stopwatch.Elapsed.TotalSeconds;
            return seconds <= 0 ? 0 : Processed / seconds;
        }
    }

    public double MegabytesPerSecond
    {
        get
        {
            double seconds = _stopwatch.Elapsed.TotalSeconds;
            return seconds <= 0 ? 0 : (BytesRead + BytesWritten) / (1024d * 1024d) / seconds;
        }
    }

    public TimeSpan? EstimatedRemaining
    {
        get
        {
            double rate = FilesPerSecond;
            if (rate <= 0 || Remaining == 0)
                return Remaining == 0 ? TimeSpan.Zero : null;

            return TimeSpan.FromSeconds(Remaining / rate);
        }
    }

    public void Start()
    {
        _stopwatch.Start();
        _lastCpuTime = _process.TotalProcessorTime;
        _lastCpuSampleAt = _stopwatch.Elapsed;
    }

    public void Stop() => _stopwatch.Stop();

    public FolderStatistics RegisterFolder(string name, int fileCount)
    {
        FolderStatistics statistics = _folders.GetOrAdd(name, key => new FolderStatistics { Name = key });
        Interlocked.Add(ref statistics.TotalFiles, fileCount);
        Interlocked.Add(ref _totalFound, fileCount);
        return statistics;
    }

    /// <summary>Corrects a folder total when the folder changed between the scan and processing.</summary>
    public void AdjustFolderTotal(string folder, int delta)
    {
        Interlocked.Add(ref _totalFound, delta);

        if (_folders.TryGetValue(folder, out FolderStatistics? statistics))
            Interlocked.Add(ref statistics.TotalFiles, delta);
    }

    public void SetCurrentFolder(string folder) => _currentFolder = folder;

    public void SetBatch(int current, int total)
    {
        _currentBatch = current;
        _totalBatches = total;
        Interlocked.Exchange(ref _errorsInCurrentBatch, 0);
    }

    /// <summary>
    /// Claims a worker slot for one file and returns its 1-based id. Slots are recycled, so the
    /// ids stay in a small range around the peak concurrency rather than climbing with the file
    /// count. Pair every call with <see cref="WorkerFinished"/>.
    /// </summary>
    public int WorkerStarted()
    {
        RecordPeak(Interlocked.Increment(ref _activeWorkers));

        return _freeSlots.TryTake(out int slot) ? slot : Interlocked.Increment(ref _slotsHandedOut);
    }

    /// <summary>
    /// Releases a worker slot and adds the time it was held to the worker-time total behind
    /// <see cref="AverageConcurrency"/>. The retry backoff counts as held time on purpose: a
    /// sleeping worker is one that cannot take the next file, which is exactly what the
    /// occupancy figure is meant to show.
    /// </summary>
    public void WorkerFinished(int slot, TimeSpan held)
    {
        // Returned before the count drops so the next worker reuses this slot rather than
        // minting a new one.
        _freeSlots.Add(slot);
        Interlocked.Add(ref _workerTicks, held.Ticks);
        Interlocked.Decrement(ref _activeWorkers);
    }

    private void RecordPeak(int active)
    {
        int peak = Volatile.Read(ref _peakActiveWorkers);

        while (active > peak)
        {
            int seen = Interlocked.CompareExchange(ref _peakActiveWorkers, active, peak);
            if (seen == peak)
                return;

            peak = seen;
        }
    }

    public void RecordRetry() => Interlocked.Increment(ref _retries);

    public void RecordSuccess(string folder, long bytesRead, long bytesWritten)
    {
        Interlocked.Increment(ref _succeeded);
        Interlocked.Add(ref _bytesRead, bytesRead);
        Interlocked.Add(ref _bytesWritten, bytesWritten);

        if (_folders.TryGetValue(folder, out FolderStatistics? statistics))
            Interlocked.Increment(ref statistics.Succeeded);
    }

    public void RecordFailure(string folder)
    {
        Interlocked.Increment(ref _failed);
        Interlocked.Increment(ref _errorsInCurrentBatch);

        if (_folders.TryGetValue(folder, out FolderStatistics? statistics))
            Interlocked.Increment(ref statistics.Failed);
    }

    public void SetFolderElapsed(string folder, TimeSpan elapsed)
    {
        if (_folders.TryGetValue(folder, out FolderStatistics? statistics))
            statistics.Elapsed = elapsed;
    }

    /// <summary>Working set of this process, in bytes.</summary>
    public long MemoryBytes
    {
        get
        {
            _process.Refresh();
            return _process.WorkingSet64;
        }
    }

    /// <summary>
    /// CPU used by this process since the previous call, as a percentage of all cores.
    /// The dashboard is the only caller, so sampling happens once per refresh.
    /// </summary>
    public double SampleCpuPercent()
    {
        _process.Refresh();
        TimeSpan cpuNow = _process.TotalProcessorTime;
        TimeSpan clockNow = _stopwatch.Elapsed;

        double windowSeconds = (clockNow - _lastCpuSampleAt).TotalSeconds;
        if (windowSeconds > 0.1)
        {
            double cpuSeconds = (cpuNow - _lastCpuTime).TotalSeconds;
            _cpuPercent = Math.Clamp(cpuSeconds / (windowSeconds * Environment.ProcessorCount) * 100d, 0d, 100d);
            _lastCpuTime = cpuNow;
            _lastCpuSampleAt = clockNow;
        }

        return _cpuPercent;
    }
}
