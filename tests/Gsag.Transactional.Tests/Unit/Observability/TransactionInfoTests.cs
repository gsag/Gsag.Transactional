using System.Transactions;
using Gsag.Transactional.Core.Observability;
using Xunit;

namespace Gsag.Transactional.Tests.Unit.Observability;

public class TransactionInfoTests
{
    [Fact]
    public void DefaultMethodName_IsEmptyString()
    {
        var info = new TransactionInfo();

        Assert.Equal(string.Empty, info.MethodName);
    }

    [Fact]
    public void DefaultDeclaringType_IsTypeOfObject()
    {
        var info = new TransactionInfo();

        Assert.Equal(typeof(object), info.DeclaringType);
    }

    [Fact]
    public void DefaultIsolationLevel_IsSerializable()
    {
        var info = new TransactionInfo();

        Assert.Equal(IsolationLevel.Serializable, info.IsolationLevel);
    }

    [Fact]
    public void DefaultPropagation_IsRequired()
    {
        var info = new TransactionInfo();

        Assert.Equal(TransactionScopeOption.Required, info.Propagation);
    }

    [Fact]
    public void DefaultTimeoutSeconds_IsNull()
    {
        var info = new TransactionInfo();

        Assert.Null(info.TimeoutSeconds);
    }

    [Fact]
    public void InitMethodName_IsPreserved()
    {
        var info = new TransactionInfo { MethodName = "DoWork" };

        Assert.Equal("DoWork", info.MethodName);
    }

    [Fact]
    public void InitDeclaringType_IsPreserved()
    {
        var info = new TransactionInfo { DeclaringType = typeof(TransactionInfoTests) };

        Assert.Equal(typeof(TransactionInfoTests), info.DeclaringType);
    }

    [Fact]
    public void InitIsolationLevel_IsPreserved()
    {
        var info = new TransactionInfo { IsolationLevel = IsolationLevel.Serializable };

        Assert.Equal(IsolationLevel.Serializable, info.IsolationLevel);
    }

    [Fact]
    public void InitPropagation_IsPreserved()
    {
        var info = new TransactionInfo { Propagation = TransactionScopeOption.RequiresNew };

        Assert.Equal(TransactionScopeOption.RequiresNew, info.Propagation);
    }

    [Fact]
    public void InitTimeoutSeconds_IsPreserved()
    {
        var info = new TransactionInfo { TimeoutSeconds = 30 };

        Assert.Equal(30, info.TimeoutSeconds);
    }

    [Fact]
    public void RecordEquality_SameValues_AreEqual()
    {
        var a = new TransactionInfo
        {
            MethodName = "Foo",
            DeclaringType = typeof(string),
            IsolationLevel = IsolationLevel.ReadCommitted,
            Propagation = TransactionScopeOption.Required,
            TimeoutSeconds = 5,
        };

        var b = new TransactionInfo
        {
            MethodName = "Foo",
            DeclaringType = typeof(string),
            IsolationLevel = IsolationLevel.ReadCommitted,
            Propagation = TransactionScopeOption.Required,
            TimeoutSeconds = 5,
        };

        Assert.Equal(a, b);
    }

    [Fact]
    public void RecordEquality_DifferentMethodName_AreNotEqual()
    {
        var a = new TransactionInfo { MethodName = "Foo" };
        var b = new TransactionInfo { MethodName = "Bar" };

        Assert.NotEqual(a, b);
    }

    [Fact]
    public void RecordEquality_DifferentDeclaringType_AreNotEqual()
    {
        var a = new TransactionInfo { DeclaringType = typeof(string) };
        var b = new TransactionInfo { DeclaringType = typeof(int) };

        Assert.NotEqual(a, b);
    }
}
