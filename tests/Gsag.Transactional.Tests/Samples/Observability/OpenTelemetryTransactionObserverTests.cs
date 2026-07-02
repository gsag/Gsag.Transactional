using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Transactions;
using Gsag.Transactional.Core.Observability;
using Gsag.Transactional.Observability.Observers;
using Xunit;

namespace Gsag.Transactional.Tests.Samples.Observability;

public class OpenTelemetryTransactionObserverTests
{
    private static readonly TransactionInfo _info = new TransactionInfo
    {
        MethodName = "Checkout",
        DeclaringType = typeof(OpenTelemetryTransactionObserverTests),
        IsolationLevel = IsolationLevel.ReadCommitted,
        Propagation = TransactionScopeOption.Required,
        TimeoutSeconds = 30,
    };

    [Fact]
    public void OnBegin_WhenActivityListenerIsRegistered_StartsActivityWithTransactionTags()
    {
        using var listener = CreateActivityListener(out var stoppedActivities);
        using var meter = new Meter("test-meter");
        using var source = new ActivitySource("test-source");
        var observer = new OpenTelemetryTransactionObserver(meter, source);

        observer.OnBegin(_info);
        observer.OnCommit(_info, TimeSpan.FromMilliseconds(12));
        observer.OnComplete(_info, committed: true, TimeSpan.FromMilliseconds(12));

        var activity = Assert.Single(stoppedActivities);
        Assert.Equal("gsag.transactional.transaction", activity.DisplayName);
        Assert.Equal("Checkout", activity.GetTagItem("gsag.transactional.method"));
        Assert.Equal(typeof(OpenTelemetryTransactionObserverTests).FullName, activity.GetTagItem("gsag.transactional.declaring_type"));
        Assert.Equal(TransactionScopeOption.Required.ToString(), activity.GetTagItem("gsag.transactional.propagation"));
        Assert.Equal(IsolationLevel.ReadCommitted.ToString(), activity.GetTagItem("gsag.transactional.isolation_level"));
        Assert.Equal(30, activity.GetTagItem("gsag.transactional.timeout_seconds"));
        Assert.Equal("committed", activity.GetTagItem("gsag.transactional.outcome"));
        Assert.Equal(true, activity.GetTagItem("gsag.transactional.committed"));
        Assert.Equal(ActivityStatusCode.Ok, activity.Status);
    }

    [Fact]
    public void OnRollback_WhenActivityListenerIsRegistered_MarksActivityAsError()
    {
        using var listener = CreateActivityListener(out var stoppedActivities);
        using var meter = new Meter("test-meter");
        using var source = new ActivitySource("test-source");
        var observer = new OpenTelemetryTransactionObserver(meter, source);
        var exception = new InvalidOperationException("payment failed");

        observer.OnBegin(_info);
        observer.OnRollback(_info, exception, TimeSpan.FromMilliseconds(8));
        observer.OnComplete(_info, committed: false, TimeSpan.FromMilliseconds(8));

        var activity = Assert.Single(stoppedActivities);
        Assert.Equal("rolled_back", activity.GetTagItem("gsag.transactional.outcome"));
        Assert.Equal(typeof(InvalidOperationException).FullName, activity.GetTagItem("exception.type"));
        Assert.Equal("payment failed", activity.GetTagItem("exception.message"));
        Assert.Equal(false, activity.GetTagItem("gsag.transactional.committed"));
        Assert.Equal(ActivityStatusCode.Error, activity.Status);
        Assert.Equal("payment failed", activity.StatusDescription);
    }

    [Fact]
    public void OnComplete_WhenNoActivityExists_DoesNotThrow()
    {
        using var meter = new Meter("test-meter");
        using var source = new ActivitySource("test-source");
        var observer = new OpenTelemetryTransactionObserver(meter, source);

        observer.OnComplete(_info, committed: true, TimeSpan.FromMilliseconds(1));
    }

    [Fact]
    public void Observer_RecordsExpectedMetricMeasurements()
    {
        using var meter = new Meter("test-meter");
        using var source = new ActivitySource("test-source");
        using var listener = CreateMeterListener(meter.Name, out var measurements);
        var observer = new OpenTelemetryTransactionObserver(meter, source);

        observer.OnBegin(_info);
        observer.OnCommit(_info, TimeSpan.FromMilliseconds(15));
        observer.OnComplete(_info, committed: true, TimeSpan.FromMilliseconds(15));

        Assert.Contains(measurements, m => m.InstrumentName == "gsag.transactional.transaction.total" && m.Value == 1);
        Assert.Contains(measurements, m => m.InstrumentName == "gsag.transactional.transaction.committed" && m.Value == 1);
        Assert.Contains(measurements, m => m.InstrumentName == "gsag.transactional.transaction.duration_ms" && m.Value == 15);
    }

    [Fact]
    public async Task Observer_WhenUsedConcurrently_DoesNotShareActivitiesAcrossAsyncFlows()
    {
        using var listener = CreateActivityListener(out var stoppedActivities);
        using var meter = new Meter("test-meter");
        using var source = new ActivitySource("test-source");
        var observer = new OpenTelemetryTransactionObserver(meter, source);

        await Task.WhenAll(
            RunObservedFlowAsync(observer, "First", committed: true),
            RunObservedFlowAsync(observer, "Second", committed: false));

        Assert.Equal(2, stoppedActivities.Count);
        var first = Assert.Single(stoppedActivities, a => a.GetTagItem("gsag.transactional.method")?.ToString() == "First");
        var second = Assert.Single(stoppedActivities, a => a.GetTagItem("gsag.transactional.method")?.ToString() == "Second");
        Assert.Equal(true, first.GetTagItem("gsag.transactional.committed"));
        Assert.Equal(false, second.GetTagItem("gsag.transactional.committed"));
    }

    [Fact]
    public void Observer_WhenTransactionsAreNestedInSameAsyncFlow_CompletesBothActivities()
    {
        using var listener = CreateActivityListener(out var stoppedActivities);
        using var meter = new Meter("test-meter");
        using var source = new ActivitySource("test-source");
        var observer = new OpenTelemetryTransactionObserver(meter, source);
        var outer = _info with { MethodName = "Outer", Propagation = TransactionScopeOption.Required };
        var inner = _info with { MethodName = "Inner", Propagation = TransactionScopeOption.RequiresNew };

        observer.OnBegin(outer);
        observer.OnBegin(inner);
        observer.OnCommit(inner, TimeSpan.FromMilliseconds(4));
        observer.OnComplete(inner, committed: true, TimeSpan.FromMilliseconds(4));
        observer.OnCommit(outer, TimeSpan.FromMilliseconds(9));
        observer.OnComplete(outer, committed: true, TimeSpan.FromMilliseconds(9));

        Assert.Equal(2, stoppedActivities.Count);
        var outerActivity = Assert.Single(stoppedActivities, a => a.GetTagItem("gsag.transactional.method")?.ToString() == "Outer");
        var innerActivity = Assert.Single(stoppedActivities, a => a.GetTagItem("gsag.transactional.method")?.ToString() == "Inner");
        Assert.Equal(true, outerActivity.GetTagItem("gsag.transactional.committed"));
        Assert.Equal(true, innerActivity.GetTagItem("gsag.transactional.committed"));
        Assert.Equal(TransactionScopeOption.Required.ToString(), outerActivity.GetTagItem("gsag.transactional.propagation"));
        Assert.Equal(TransactionScopeOption.RequiresNew.ToString(), innerActivity.GetTagItem("gsag.transactional.propagation"));
    }
    private static async Task RunObservedFlowAsync(
        OpenTelemetryTransactionObserver observer,
        string methodName,
        bool committed)
    {
        var info = _info with { MethodName = methodName };

        observer.OnBegin(info);
        await Task.Delay(10);

        if (committed)
        {
            observer.OnCommit(info, TimeSpan.FromMilliseconds(10));
        }
        else
        {
            observer.OnRollback(info, new InvalidOperationException(methodName), TimeSpan.FromMilliseconds(10));
        }

        observer.OnComplete(info, committed, TimeSpan.FromMilliseconds(10));
    }

    private static ActivityListener CreateActivityListener(out ConcurrentBag<Activity> stoppedActivities)
    {
        var activities = new ConcurrentBag<Activity>();
        stoppedActivities = activities;

        var listener = new ActivityListener
        {
            ShouldListenTo = _ => true,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = activity => activities.Add(activity),
        };

        ActivitySource.AddActivityListener(listener);
        return listener;
    }

    private static MeterListener CreateMeterListener(
        string meterName,
        out List<(string InstrumentName, double Value)> measurements)
    {
        var recordedMeasurements = new List<(string InstrumentName, double Value)>();
        measurements = recordedMeasurements;

        var listener = new MeterListener
        {
            InstrumentPublished = (instrument, meterListener) =>
            {
                if (instrument.Meter.Name == meterName)
                {
                    meterListener.EnableMeasurementEvents(instrument);
                }
            },
        };

        listener.SetMeasurementEventCallback<long>((instrument, value, _, _) =>
            recordedMeasurements.Add((instrument.Name, value)));
        listener.SetMeasurementEventCallback<double>((instrument, value, _, _) =>
            recordedMeasurements.Add((instrument.Name, value)));

        listener.Start();
        return listener;
    }
}
