using LoadTest.Observers;

namespace LoadTest.Validation;

class LifecycleAccumulator
{
    private readonly List<(int Scenario, string Error)> _errors = new();

    public void CaptureErrors(int scenarioNumber, ConsistencyCheckResult result)
    {
        foreach (var error in result.OrphanedTransactions
            .Concat(result.IncompleteTransactions)
            .Concat(result.InvalidTransitions))
        {
            _errors.Add((scenarioNumber, error));
        }
    }

    public bool HasErrors => _errors.Count > 0;

    public IReadOnlyList<(int Scenario, string Error)> Errors => _errors.AsReadOnly();
}
