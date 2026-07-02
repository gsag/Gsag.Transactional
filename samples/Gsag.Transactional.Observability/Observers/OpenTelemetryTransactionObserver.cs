using System.Diagnostics;
using System.Diagnostics.Metrics;
using Gsag.Transactional.Core.Observability;
using Microsoft.Extensions.DependencyInjection;

namespace Gsag.Transactional.Observability.Observers;

/// <summary>
/// Records transactional lifecycle events using .NET diagnostics primitives consumed by OpenTelemetry.
/// </summary>
public sealed class OpenTelemetryTransactionObserver : ITransactionObserver, IDisposable
{
    private readonly Meter _meter;
    private readonly ActivitySource _activitySource;
    private readonly Counter<long> _totalCounter;
    private readonly Counter<long> _committedCounter;
    private readonly Counter<long> _rolledBackCounter;
    private readonly Histogram<double> _durationHistogram;
    private readonly AsyncLocal<ActivityFrame?> _currentFrame = new();
    private readonly bool _ownsDiagnostics;

    /// <summary>
    /// Initializes a new observer with the default transactional instrumentation name.
    /// </summary>
    [ActivatorUtilitiesConstructor]
    public OpenTelemetryTransactionObserver()
        : this(
            new Meter(OpenTelemetryConventions.InstrumentationName),
            new ActivitySource(OpenTelemetryConventions.InstrumentationName),
            ownsDiagnostics: true)
    {
    }

    /// <summary>
    /// Initializes a new observer that records transactions through the provided diagnostics primitives.
    /// </summary>
    public OpenTelemetryTransactionObserver(Meter meter, ActivitySource activitySource)
        : this(meter, activitySource, ownsDiagnostics: false)
    {
    }

    private OpenTelemetryTransactionObserver(Meter meter, ActivitySource activitySource, bool ownsDiagnostics)
    {
        _meter = meter;
        _activitySource = activitySource;
        _ownsDiagnostics = ownsDiagnostics;
        _totalCounter = meter.CreateCounter<long>(OpenTelemetryConventions.Metrics.TransactionTotal);
        _committedCounter = meter.CreateCounter<long>(OpenTelemetryConventions.Metrics.TransactionCommitted);
        _rolledBackCounter = meter.CreateCounter<long>(OpenTelemetryConventions.Metrics.TransactionRolledBack);
        _durationHistogram = meter.CreateHistogram<double>(OpenTelemetryConventions.Metrics.TransactionDurationMs);
    }

    /// <inheritdoc />
    public void OnBegin(TransactionInfo info)
    {
        var tags = CreateTags(info);
        _totalCounter.Add(1, tags);

        var activity = _activitySource.StartActivity(OpenTelemetryConventions.Activities.Transaction, ActivityKind.Internal);
        if (activity is not null)
        {
            SetTags(activity, tags);
        }

        _currentFrame.Value = new ActivityFrame(activity, _currentFrame.Value);
    }

    /// <inheritdoc />
    public void OnCommit(TransactionInfo info, TimeSpan elapsed)
    {
        var tags = CreateTags(info);
        _committedCounter.Add(1, tags);

        Activity? activity = _currentFrame.Value?.Activity;
        activity?.SetTag(OpenTelemetryConventions.Tags.Outcome, OpenTelemetryConventions.Outcomes.Committed);
        activity?.SetStatus(ActivityStatusCode.Ok);
    }

    /// <inheritdoc />
    public void OnRollback(TransactionInfo info, Exception exception, TimeSpan elapsed)
    {
        var tags = CreateTags(info);
        _rolledBackCounter.Add(1, tags);

        Activity? activity = _currentFrame.Value?.Activity;
        activity?.SetTag(OpenTelemetryConventions.Tags.Outcome, OpenTelemetryConventions.Outcomes.RolledBack);
        activity?.SetTag(OpenTelemetryConventions.Tags.ExceptionType, exception.GetType().FullName);
        activity?.SetTag(OpenTelemetryConventions.Tags.ExceptionMessage, exception.Message);
        activity?.SetStatus(ActivityStatusCode.Error, exception.Message);
    }

    /// <inheritdoc />
    public void OnComplete(TransactionInfo info, bool committed, TimeSpan elapsed)
    {
        var tags = CreateTags(info, (OpenTelemetryConventions.Tags.Committed, committed));
        _durationHistogram.Record(elapsed.TotalMilliseconds, tags);

        ActivityFrame? frame = _currentFrame.Value;
        if (frame is null)
        {
            return;
        }

        Activity? activity = frame.Activity;
        if (activity is not null)
        {
            activity.SetTag(OpenTelemetryConventions.Tags.Committed, committed);
            activity.SetTag(OpenTelemetryConventions.Tags.DurationMs, elapsed.TotalMilliseconds);
            activity.Stop();
        }

        _currentFrame.Value = frame.Parent;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (!_ownsDiagnostics)
        {
            return;
        }

        _activitySource.Dispose();
        _meter.Dispose();
    }

    private static KeyValuePair<string, object?>[] CreateTags(
        TransactionInfo info,
        params (string Key, object? Value)[] additionalTags)
    {
        var tagCount = info.TimeoutSeconds.HasValue ? 5 : 4;
        var tags = new KeyValuePair<string, object?>[tagCount + additionalTags.Length];

        tags[0] = new(OpenTelemetryConventions.Tags.Method, info.MethodName);
        tags[1] = new(OpenTelemetryConventions.Tags.DeclaringType, info.DeclaringType.FullName);
        tags[2] = new(OpenTelemetryConventions.Tags.Propagation, info.Propagation.ToString());
        tags[3] = new(OpenTelemetryConventions.Tags.IsolationLevel, info.IsolationLevel.ToString());

        var index = 4;
        if (info.TimeoutSeconds.HasValue)
        {
            tags[index] = new(OpenTelemetryConventions.Tags.TimeoutSeconds, info.TimeoutSeconds.Value);
            index++;
        }

        foreach ((string key, object? value) in additionalTags)
        {
            tags[index] = new(key, value);
            index++;
        }

        return tags;
    }

    private static void SetTags(Activity activity, IEnumerable<KeyValuePair<string, object?>> tags)
    {
        foreach (KeyValuePair<string, object?> tag in tags)
        {
            activity.SetTag(tag.Key, tag.Value);
        }
    }

    private sealed record ActivityFrame(Activity? Activity, ActivityFrame? Parent);
}
