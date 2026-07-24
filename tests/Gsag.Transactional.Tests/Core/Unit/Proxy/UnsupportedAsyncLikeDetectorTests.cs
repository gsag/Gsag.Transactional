using System.Runtime.CompilerServices;
using Gsag.Transactional.Core.Proxy;
using Xunit;

namespace Gsag.Transactional.Tests.Core.Unit.Proxy;

public sealed class UnsupportedAsyncLikeDetectorTests
{
#pragma warning disable CA1822 // Members must remain instance-level to satisfy reflection-based awaiter pattern detection via BindingFlags.Instance
    private sealed class HalfAwaitable
    {
        public HalfAwaiter GetAwaiter() => new();
    }

    private sealed class HalfAwaiter
    {
        public bool IsCompleted => true;

        public int GetResult() => 42;

        public void OnCompleted(Action continuation) => continuation();
    }
#pragma warning restore CA1822

    [Fact]
    public void CanHandle_WhenAwaiterHasGetResultButNoINotifyCompletion_ReturnsFalse()
    {
        Assert.False(UnsupportedAsyncLikeDetector.IsUnsupportedAsyncLikeReturnType(typeof(HalfAwaitable)));
    }
}
