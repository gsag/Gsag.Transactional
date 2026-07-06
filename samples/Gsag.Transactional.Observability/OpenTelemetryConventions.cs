namespace Gsag.Transactional.Observability;

public static class OpenTelemetryConventions
{
    private const string DomainPrefix = "gsag.transactional";
    private const string TransactionPrefix = $"{DomainPrefix}.transaction";
    private const string TagPrefix = DomainPrefix;

    public const string InstrumentationName = DomainPrefix;

    public static class Configuration
    {
        public const string SectionName = "Observability";
        public const string TracesEndpoint = "Traces.Endpoint";
        public const string MetricsEndpoint = "Metrics.Endpoint";
    }

    public static class Activities
    {
        public const string Transaction = TransactionPrefix;
    }

    public static class Metrics
    {
        public const string TransactionTotal = $"{TransactionPrefix}.total";
        public const string TransactionCommitted = $"{TransactionPrefix}.committed";
        public const string TransactionRolledBack = $"{TransactionPrefix}.rolled_back";
        public const string TransactionDurationMs = $"{TransactionPrefix}.duration_ms";
    }

    public static class Tags
    {
        public const string Method = $"{TagPrefix}.method";
        public const string DeclaringType = $"{TagPrefix}.declaring_type";
        public const string Propagation = $"{TagPrefix}.propagation";
        public const string IsolationLevel = $"{TagPrefix}.isolation_level";
        public const string TimeoutSeconds = $"{TagPrefix}.timeout_seconds";
        public const string Outcome = $"{TagPrefix}.outcome";
        public const string Committed = $"{TagPrefix}.committed";
        public const string DurationMs = $"{TagPrefix}.duration_ms";
        public const string ExceptionType = "exception.type";
        public const string ExceptionMessage = "exception.message";
    }

    public static class Database
    {
        public const string SaveChangesActivity = $"{DomainPrefix}.database.save_changes";
        public const string DbSystem = "db.system";
        public const string Operation = "db.operation";
        public const string AffectedEntries = $"{TagPrefix}.database.affected_entries";
        public const string AffectedRows = $"{TagPrefix}.database.affected_rows";
        public const string PostgresSystem = "postgresql";
        public const string SaveChangesOperation = "save_changes";
    }

    public static class Outcomes
    {
        public const string Committed = "committed";
        public const string RolledBack = "rolled_back";
    }
}
