using Gsag.Transactional.Core.Attributes;
using Gsag.Transactional.Core.Proxy;
using Xunit;

namespace Gsag.Transactional.Tests.Unit.Proxy;

public class RollbackPolicyTests
{
    private static RollbackPolicy Policy(Type[]? noRollbackFor = null, Type[]? rollbackFor = null) =>
        RollbackPolicy.From(new TransactionalAttribute
        {
            NoRollbackFor = noRollbackFor ?? [],
            RollbackFor = rollbackFor ?? [],
        });

    [Fact]
    public void Default_AnyException_ShouldRollback()
    {
        var policy = Policy();

        Assert.True(policy.ShouldRollback(new Exception()));
    }

    [Fact]
    public void NoRollbackFor_ExactMatch_ReturnsFalse()
    {
        var policy = Policy(noRollbackFor: [typeof(OperationCanceledException)]);

        Assert.False(policy.ShouldRollback(new OperationCanceledException()));
    }

    [Fact]
    public void NoRollbackFor_NoMatch_ReturnsTrue()
    {
        var policy = Policy(noRollbackFor: [typeof(OperationCanceledException)]);

        Assert.True(policy.ShouldRollback(new InvalidOperationException()));
    }

    [Fact]
    public void NoRollbackFor_SubclassMatch_ReturnsFalse()
    {
        var policy = Policy(noRollbackFor: [typeof(OperationCanceledException)]);

        Assert.False(policy.ShouldRollback(new TaskCanceledException())); // subclass of OperationCanceledException
    }

    [Fact]
    public void RollbackFor_ExactMatch_ReturnsTrue()
    {
        var policy = Policy(rollbackFor: [typeof(InvalidOperationException)]);

        Assert.True(policy.ShouldRollback(new InvalidOperationException()));
    }

    [Fact]
    public void RollbackFor_NoMatch_ReturnsFalse()
    {
        var policy = Policy(rollbackFor: [typeof(InvalidOperationException)]);

        Assert.False(policy.ShouldRollback(new ArgumentException()));
    }

    [Fact]
    public void RollbackFor_SubclassOfListedType_ReturnsTrue()
    {
        var policy = Policy(rollbackFor: [typeof(IOException)]);

        Assert.True(policy.ShouldRollback(new FileNotFoundException())); // subclass of IOException
    }

    [Fact]
    public void Conflict_SameTypeInBothLists_NoRollbackForWins()
    {
        var policy = Policy(
            noRollbackFor: [typeof(InvalidOperationException)],
            rollbackFor: [typeof(InvalidOperationException)]);

        Assert.False(policy.ShouldRollback(new InvalidOperationException()));
    }

    [Fact]
    public void NoRollbackFor_MultipleTypes_MatchesAny()
    {
        var policy = Policy(noRollbackFor: [typeof(OperationCanceledException), typeof(ArgumentException)]);

        Assert.False(policy.ShouldRollback(new OperationCanceledException()));
        Assert.False(policy.ShouldRollback(new ArgumentException()));
        Assert.True(policy.ShouldRollback(new InvalidOperationException()));
    }
}
