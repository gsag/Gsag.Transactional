namespace Transactional.Core.Hooks;

internal enum HookEvent
{
    AfterCommit,
    AfterRollback,
    AfterCompletion,
}
