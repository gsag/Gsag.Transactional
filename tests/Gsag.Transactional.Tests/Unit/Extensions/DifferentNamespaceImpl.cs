using Gsag.Transactional.Core.Attributes;
using Gsag.Transactional.Tests.Unit.Extensions.Other;

// Concrete class is in the standard Extensions namespace — its interface is in
// Extensions.Other — so the I{ClassName} namespace check deliberately fails.
namespace Gsag.Transactional.Tests.Unit.Extensions;

public class DifferentNamespaceService : IDifferentNamespaceService
{
    [Transactional]
    public string Do() => "different-ns";
}
