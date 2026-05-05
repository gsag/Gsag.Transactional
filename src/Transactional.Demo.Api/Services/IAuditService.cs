using Transactional.Demo.Api.Entities;

namespace Transactional.Demo.Api.Services;

public interface IAuditService
{
    /// <summary>
    /// Writes an audit entry in a RequiresNew scope — independent of any ambient transaction.
    /// Persists even if the caller's transaction rolls back.
    /// </summary>
    Task<AuditEntry> WriteAsync(string action, string scenario, bool succeeded);

    Task<IEnumerable<AuditEntry>> GetAllAsync();
}
