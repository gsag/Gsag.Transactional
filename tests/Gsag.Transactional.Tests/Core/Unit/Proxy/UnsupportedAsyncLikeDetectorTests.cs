using System.Runtime.CompilerServices;
using Gsag.Transactional.Core.Proxy;
using Xunit;

namespace Gsag.Transactional.Tests.Core.Unit.Proxy;

public sealed class UnsupportedAsyncLikeDetectorTests
{
#pragma warning disable IDE0051 // Members must be instance-level to satisfy reflection-based awaiter pattern detection
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
#pragma warning restore IDE0051

    [Fact]
    public void CanHandle_WhenAwaiterHasGetResultButNoINotifyCompletion_ReturnsFalse()
    {
        Assert.False(UnsupportedAsyncLikeDetector.IsUnsupportedAsyncLikeReturnType(typeof(HalfAwaitable)));
    }
}
