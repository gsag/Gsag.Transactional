namespace Gsag.Transactional.Demo.Api.Exceptions;

public class InventoryException : Exception
{
    public InventoryException() { }
    public InventoryException(string message) : base(message) { }
    public InventoryException(string message, Exception innerException) : base(message, innerException) { }
}
