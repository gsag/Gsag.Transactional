namespace Transactional.Demo.Api.Exceptions;

public class PaymentDeclinedException : Exception
{
    public PaymentDeclinedException(string message) : base(message) { }
}
