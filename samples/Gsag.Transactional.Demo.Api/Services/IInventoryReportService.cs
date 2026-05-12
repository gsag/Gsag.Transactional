namespace Gsag.Transactional.Demo.Api.Services;

public record InventoryReport(int TotalReservations, string Note);

public interface IInventoryReportService
{
    /// <summary>
    /// Reads inventory data with Suppress propagation — runs outside any ambient transaction.
    /// Transaction.Current is null for the duration of this call.
    /// </summary>
    Task<InventoryReport> ReadAvailableStockAsync(CancellationToken ct = default);
}
