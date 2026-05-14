namespace Gsag.Transactional.Core.Hooks;

internal enum HookEvent
{
    BeforeCommit,
    AfterCommit,
    BeforeRollback,
    AfterRollback,
    AfterCompletion,
}
