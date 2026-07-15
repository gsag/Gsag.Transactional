namespace Gsag.Transactional.Observability;

/// <summary>
/// OpenTelemetry semantic conventions for the transactional observability pipeline.
/// </summary>
public static class OpenTelemetryConventions
{
    private const string DomainPrefix = "gsag.transactional";
    private const string TransactionPrefix = $"{DomainPrefix}.transaction";
    private const string TagPrefix = DomainPrefix;

    /// <summary>
    /// Instrumentation name used to identify telemetry produced by this library.
    /// </summary>
    public const string InstrumentationName = DomainPrefix;

    /// <summary>
    /// Configuration key conventions.
    /// </summary>
    public static class Configuration
    {
        /// <summary>
        /// Configuration section name for observability options.
        /// </summary>
        public const string SectionName = "Observability";

        /// <summary>
        /// Configuration key for the traces OTLP endpoint.
        /// </summary>
        public const string TracesEndpoint = "Traces.Endpoint";

        /// <summary>
        /// Configuration key for the metrics OTLP endpoint.
        /// </summary>
        public const string MetricsEndpoint = "Metrics.Endpoint";

        /// <summary>
        /// Configuration key for the logs OTLP endpoint.
        /// </summary>
        public const string LogsEndpoint = "Logs.Endpoint";
    }

    /// <summary>
    /// Activity source names for distributed tracing.
    /// </summary>
    public static class Activities
    {
        /// <summary>
        /// Activity name for transaction lifecycle spans.
        /// </summary>
        public const string Transaction = TransactionPrefix;
    }

    /// <summary>
    /// Metric instrument names.
    /// </summary>
    public static class Metrics
    {
        /// <summary>
        /// Counter for total transactions started.
        /// </summary>
        public const string TransactionTotal = $"{TransactionPrefix}.total";

        /// <summary>
        /// Counter for committed transactions.
        /// </summary>
        public const string TransactionCommitted = $"{TransactionPrefix}.committed";

        /// <summary>
        /// Counter for rolled-back transactions.
        /// </summary>
        public const string TransactionRolledBack = $"{TransactionPrefix}.rolled_back";

        /// <summary>
        /// Histogram for transaction duration in milliseconds.
        /// </summary>
        public const string TransactionDurationMs = $"{TransactionPrefix}.duration_ms";
    }

    /// <summary>
    /// Tag key conventions for activities and metrics.
    /// </summary>
    public static class Tags
    {
        /// <summary>Transaction method name.</summary>
        public const string Method = $"{TagPrefix}.method";

        /// <summary>Declaring type full name.</summary>
        public const string DeclaringType = $"{TagPrefix}.declaring_type";

        /// <summary>Transaction scope propagation option.</summary>
        public const string Propagation = $"{TagPrefix}.propagation";

        /// <summary>Transaction isolation level.</summary>
        public const string IsolationLevel = $"{TagPrefix}.isolation_level";

        /// <summary>Transaction timeout in seconds.</summary>
        public const string TimeoutSeconds = $"{TagPrefix}.timeout_seconds";

        /// <summary>Transaction outcome (committed or rolled_back).</summary>
        public const string Outcome = $"{TagPrefix}.outcome";

        /// <summary>Whether the transaction was committed.</summary>
        public const string Committed = $"{TagPrefix}.committed";

        /// <summary>Transaction duration in milliseconds.</summary>
        public const string DurationMs = $"{TagPrefix}.duration_ms";

        /// <summary>Exception type full name.</summary>
        public const string ExceptionType = "exception.type";

        /// <summary>Exception message.</summary>
        public const string ExceptionMessage = "exception.message";
    }

    /// <summary>
    /// Database-related telemetry conventions.
    /// </summary>
    public static class Database
    {
        /// <summary>Activity source name for database operations.</summary>
        public const string ActivitySourceName = $"{DomainPrefix}.database";

        /// <summary>Activity name for SaveChanges operations.</summary>
        public const string SaveChangesActivity = $"{ActivitySourceName}.save_changes";

        /// <summary>Database system attribute (OpenTelemetry semantic convention).</summary>
        public const string DbSystem = "db.system";

        /// <summary>Database operation attribute.</summary>
        public const string Operation = "db.operation";

        /// <summary>Number of entity entries affected.</summary>
        public const string AffectedEntries = $"{TagPrefix}.database.affected_entries";

        /// <summary>Number of database rows affected.</summary>
        public const string AffectedRows = $"{TagPrefix}.database.affected_rows";

        /// <summary>PostgreSQL system identifier.</summary>
        public const string PostgresSystem = "postgresql";

        /// <summary>SaveChanges operation identifier.</summary>
        public const string SaveChangesOperation = "save_changes";
    }

    /// <summary>
    /// Transaction outcome identifiers.
    /// </summary>
    public static class Outcomes
    {
        /// <summary>Transaction committed successfully.</summary>
        public const string Committed = "committed";

        /// <summary>Transaction was rolled back.</summary>
        public const string RolledBack = "rolled_back";
    }
}
