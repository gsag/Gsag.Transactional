namespace Gsag.Transactional.Demo.Api.Exceptions;

public class PaymentDeclinedException : Exception
{
    public PaymentDeclinedException() { }
    public PaymentDeclinedException(string message) : base(message) { }
    public PaymentDeclinedException(string message, Exception innerException) : base(message, innerException) { }
}
