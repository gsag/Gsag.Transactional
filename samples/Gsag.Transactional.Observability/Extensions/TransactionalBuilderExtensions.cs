using Gsag.Transactional.Core.Extensions;
using Gsag.Transactional.Observability.Observers;

namespace Gsag.Transactional.Observability.Extensions;

/// <summary>
/// Extension methods for enabling transactional observability.
/// </summary>
public static class TransactionalBuilderExtensions
{
    /// <summary>
    /// Adds transactional observability instrumentation to the configured transactional services.
    /// </summary>
    public static ITransactionalBuilder AddObservability(this ITransactionalBuilder builder) =>
        builder.AddObserver<OpenTelemetryTransactionObserver>();
}
