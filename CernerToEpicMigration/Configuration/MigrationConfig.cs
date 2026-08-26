namespace CernerToEpicMigration.Configuration;

/// <summary>
/// Root configuration section (<c>MigrationConfig</c> in appsettings.json).
/// Only the Stage 1 (XHTML -> RTF) settings are represented here; the HL7
/// settings described in the design document belong to Stage 2 and are not
/// bound by this build.
/// </summary>
public sealed class MigrationConfig
{
    public const string SectionName = "MigrationConfig";

    /// <summary>Root folder holding the date-wise input folders.</summary>
    public string InputBasePath { get; set; } = string.Empty;

    /// <summary>Root folder for the converted RTF files (date structure is mirrored).</summary>
    public string OutputRtfBasePath { get; set; } = string.Empty;

    /// <summary>Folder for the summary/error CSV reports and the checkpoint file.</summary>
    public string ReportBasePath { get; set; } = string.Empty;

    /// <summary>Folder for the rolling Serilog log files.</summary>
    public string LogBasePath { get; set; } = string.Empty;

    public int LogRetainedFileCountLimit { get; set; } = 30;

    public ProcessingOptions Processing { get; set; } = new();

    public DashboardOptions Dashboard { get; set; } = new();

    /// <summary>
    /// Validates the configuration and returns one message per problem found.
    /// An empty list means the configuration is usable.
    /// </summary>
    public IReadOnlyList<string> Validate()
    {
        List<string> errors = new();

        if (string.IsNullOrWhiteSpace(InputBasePath))
            errors.Add("InputBasePath is not configured.");
        else if (!Directory.Exists(InputBasePath))
            errors.Add($"InputBasePath does not exist: {InputBasePath}");

        if (string.IsNullOrWhiteSpace(OutputRtfBasePath))
            errors.Add("OutputRtfBasePath is not configured.");

        if (string.IsNullOrWhiteSpace(ReportBasePath))
            errors.Add("ReportBasePath is not configured.");

        if (string.IsNullOrWhiteSpace(LogBasePath))
            errors.Add("LogBasePath is not configured.");

        if (LogRetainedFileCountLimit <= 0)
            errors.Add("LogRetainedFileCountLimit must be greater than zero.");

        if (Processing.BatchSize <= 0)
            errors.Add("Processing.BatchSize must be greater than zero.");

        if (Processing.MaxRetryCount < 1)
            errors.Add("Processing.MaxRetryCount must be at least 1 (1 = a single attempt, no retries).");

        if (Processing.RetryDelayMs < 0)
            errors.Add("Processing.RetryDelayMs cannot be negative.");

        if (Processing.MaxDegreeOfParallelism < 0)
            errors.Add("Processing.MaxDegreeOfParallelism cannot be negative (0 = auto-detect).");

        if (Processing.ConversionTimeoutSeconds <= 0)
            errors.Add("Processing.ConversionTimeoutSeconds must be greater than zero.");

        if (string.IsNullOrWhiteSpace(Processing.FileSearchPattern))
            errors.Add("Processing.FileSearchPattern is not configured.");

        if (Processing.MaxErrorLogFiles < 0)
            errors.Add("Processing.MaxErrorLogFiles cannot be negative (0 = no cap).");

        if (Dashboard.RefreshIntervalSeconds <= 0)
            errors.Add("Dashboard.RefreshIntervalSeconds must be greater than zero.");

        return errors;
    }
}

/// <summary>Threading, batching and retry settings (design document section 9.2).</summary>
public sealed class ProcessingOptions
{
    /// <summary>Concurrent conversions. 0 means auto-detect (<see cref="Environment.ProcessorCount"/>).</summary>
    public int MaxDegreeOfParallelism { get; set; }

    /// <summary>Files per batch. A checkpoint is written after every completed batch.</summary>
    public int BatchSize { get; set; } = 1000;

    /// <summary>Total attempts per file before it is moved to the error folder.</summary>
    public int MaxRetryCount { get; set; } = 3;

    /// <summary>Base delay between attempts; the delay is multiplied by the attempt number.</summary>
    public int RetryDelayMs { get; set; } = 500;

    /// <summary>Search pattern used to pick up input documents, e.g. <c>*.xhtml</c>.</summary>
    public string FileSearchPattern { get; set; } = "*.xhtml";

    /// <summary>Guard against a single pathological document stalling a worker thread.</summary>
    public int ConversionTimeoutSeconds { get; set; } = 60;

    /// <summary>Move successfully converted input files to <c>{dateFolder}\archive</c>.</summary>
    public bool ArchiveOnSuccess { get; set; } = true;

    /// <summary>Overwrite an existing RTF file. When false, an existing output is treated as a failure.</summary>
    public bool OverwriteExistingRtf { get; set; } = true;

    /// <summary>
    /// Feature flag: write the RTF wrapped in a Base64 envelope instead of as plain RTF,
    /// mirroring the envelope the input documents arrive in. The file name and the
    /// <c>.rtf</c> extension are unchanged - only the bytes inside differ - so a consumer
    /// that expects plain RTF must be switched over at the same time. Off means plain RTF.
    /// </summary>
    public bool EncodeRtfOutputAsBase64 { get; set; }

    /// <summary>Kept for parity with the design document; Stage 1 is the only stage in this build.</summary>
    public bool EnableStage1 { get; set; } = true;

    /// <summary>
    /// Count every input file before conversion starts, so the progress bar and the
    /// remaining-time estimate cover the whole run. This costs one extra directory walk of the
    /// entire input tree, which is minutes rather than seconds on a network share; turn it off
    /// there and the totals fill in per date folder as the run reaches them.
    /// </summary>
    public bool PreScanForEstimates { get; set; } = true;

    /// <summary>
    /// Cap on per-file <c>.error.log</c> files written across a run. 0 means no cap. Every
    /// failure stays in the error report CSV and the run log regardless.
    /// </summary>
    public int MaxErrorLogFiles { get; set; }

    /// <summary>
    /// Write one row per document to <c>file_trace_{timestamp}.csv</c>: worker slot, thread,
    /// start, end, duration, attempts and outcome. This is how a tuning run proves the configured
    /// number of workers was actually busy. About 110
    /// bytes a row, so a 50,000-file tuning run costs ~5 MB and a million-file production run
    /// ~110 MB; leave it off for the bulk run unless the trace is wanted.
    /// </summary>
    public bool EnableFileTrace { get; set; }

    /// <summary>Effective worker count after resolving the auto-detect value.</summary>
    public int EffectiveParallelism =>
        MaxDegreeOfParallelism > 0 ? MaxDegreeOfParallelism : Environment.ProcessorCount;

    public TimeSpan ConversionTimeout => TimeSpan.FromSeconds(ConversionTimeoutSeconds);
}

/// <summary>Console dashboard settings (design document section 11.1).</summary>
public sealed class DashboardOptions
{
    public int RefreshIntervalSeconds { get; set; } = 5;

    public bool EnableConsoleDashboard { get; set; } = true;
}
