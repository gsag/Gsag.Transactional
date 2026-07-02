namespace Gsag.Transactional.Observability;

internal static class OpenTelemetryConventions
{
    private const string DomainPrefix = "gsag.transactional";
    private const string TransactionPrefix = $"{DomainPrefix}.transaction";
    private const string TagPrefix = DomainPrefix;

    internal const string InstrumentationName = DomainPrefix;

    internal static class Activities
    {
        internal const string Transaction = TransactionPrefix;
    }

    internal static class Metrics
    {
        internal const string TransactionTotal = $"{TransactionPrefix}.total";
        internal const string TransactionCommitted = $"{TransactionPrefix}.committed";
        internal const string TransactionRolledBack = $"{TransactionPrefix}.rolled_back";
        internal const string TransactionDurationMs = $"{TransactionPrefix}.duration_ms";
    }

    internal static class Tags
    {
        internal const string Method = $"{TagPrefix}.method";
        internal const string DeclaringType = $"{TagPrefix}.declaring_type";
        internal const string Propagation = $"{TagPrefix}.propagation";
        internal const string IsolationLevel = $"{TagPrefix}.isolation_level";
        internal const string TimeoutSeconds = $"{TagPrefix}.timeout_seconds";
        internal const string Outcome = $"{TagPrefix}.outcome";
        internal const string Committed = $"{TagPrefix}.committed";
        internal const string DurationMs = $"{TagPrefix}.duration_ms";
        internal const string ExceptionType = "exception.type";
        internal const string ExceptionMessage = "exception.message";
    }

    internal static class Outcomes
    {
        internal const string Committed = "committed";
        internal const string RolledBack = "rolled_back";
    }
}
