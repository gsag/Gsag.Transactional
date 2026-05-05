namespace Transactional.Demo.Api.Exceptions;

public class NotificationException : Exception
{
    public NotificationException() { }
    public NotificationException(string message) : base(message) { }
    public NotificationException(string message, Exception innerException) : base(message, innerException) { }
}
