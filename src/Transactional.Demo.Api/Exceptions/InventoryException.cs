namespace Transactional.Demo.Api.Exceptions;

public class InventoryException : Exception
{
    public InventoryException(string message) : base(message) { }
}
