using System.Reflection;
using Gsag.Transactional.Core.Attributes;
using Gsag.Transactional.Core.Extensions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Gsag.Transactional.Tests.Core.Unit.Extensions;

public interface IAbstractTransactionalService
{
    string Run();
}

public abstract class AbstractTransactionalService : IAbstractTransactionalService
{
    [Transactional]
    public virtual string Run() => "abstract";
}

public sealed class ConcreteTransactionalService : AbstractTransactionalService
{
    public override string Run() => "concrete";
}

public class TransactionalBuilderGapTests
{
    [Fact]
    public void ScanAssembly_DoesNotRegisterAbstractTransactionalServices()
    {
        var services = new ServiceCollection();

        services.AddTransactional(builder => builder.ScanAssembly(Assembly.GetExecutingAssembly()));

        Assert.DoesNotContain(services, descriptor => descriptor.ServiceType == typeof(AbstractTransactionalService));
        Assert.DoesNotContain(services, descriptor => descriptor.ServiceType == typeof(IAbstractTransactionalService));
    }
}
