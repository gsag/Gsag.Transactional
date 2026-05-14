using Gsag.Transactional.Core.Attributes;

namespace Gsag.Transactional.Core.Proxy;

internal sealed class RollbackPolicy
{
    private readonly Type[] _noRollbackFor;
    private readonly Type[] _rollbackFor;

    private RollbackPolicy(Type[] noRollbackFor, Type[] rollbackFor)
    {
        _noRollbackFor = noRollbackFor;
        _rollbackFor = rollbackFor;
    }

    internal static RollbackPolicy From(TransactionalAttribute attr) =>
        new(attr.NoRollbackFor, attr.RollbackFor);

    internal bool ShouldRollback(Exception ex)
    {
        if (_noRollbackFor.Length > 0 && IsMatch(ex, _noRollbackFor))
        {
            return false;
        }

        if (_rollbackFor.Length > 0)
        {
            return IsMatch(ex, _rollbackFor);
        }

        return true;
    }

    private static bool IsMatch(Exception ex, Type[] types) =>
        types.Any(t => t.IsAssignableFrom(ex.GetType()));
}
