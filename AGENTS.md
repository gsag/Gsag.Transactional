# Gsag.Transactional

Declarative transaction management for .NET using native runtime primitives only.

Core technologies:
- DispatchProxy
- TransactionScope
- AsyncLocal
- Reflection
- Expressions

# Project Principles

- Native .NET only
- No external AOP frameworks
- Predictable transactional behavior
- Minimal hidden runtime behavior
- Consistent async transaction flow
- Lifecycle correctness over abstraction reduction

# Critical Invariants

## Async Flow

Always use:
- TransactionScopeAsyncFlowOption.Enabled

Reason:
- preserve ambient transaction across await

## Transaction Ordering

Transaction scope must exist BEFORE target invocation.

Reason:
- dependencies must open connections inside the ambient transaction

## Sync Path

Synchronous flows must remain fully synchronous.

Never:
- use .GetAwaiter().GetResult()
- route sync flows through async wrappers

## Self Invocation

Self-invocation bypasses proxy interception.

Never:
```csharp
this.TransactionalMethod();
```

# Behavioral Constraints

Preserve during refactors:
- rollback consistency
- observer ordering
- hook ordering
- nested propagation behavior
- ambient transaction restoration
- exception propagation semantics

Behavioral correctness is more important than preserving current class structure.

# Architectural Constraints

Keep transactional interception separated from:
- transaction lifecycle
- rollback decisions
- observer notifications
- hook execution

Routing/caching responsibilities must not own transaction logic.

# Error Handling Rules

Dispose failures must NOT suppress:
- rollback notifications
- observer execution
- hook execution
- original exceptions

# Testing Rules

Always run:
```bash
dotnet test
```

After changes involving:
- transaction lifecycle
- propagation
- async flow
- rollback behavior
- hooks
- observers
- interception

For concurrency and load validation, run:
```bat
scripts\load-test\load-test.bat
```

Covers:
- throughput under high concurrency
- rollback vs commit correctness under load
- AsyncLocal hook isolation across concurrent tasks
- nested RequiresNew propagation correctness

# Code Style

Always use braces for:
- if
- for
- foreach
- while

All new code and refactors must strictly follow this premises:
- Always ask or clarify if the prompt is ambiguous; do not make decisions due to lack of clarity
- Always create an execution plan before any action and review it with the user
- Always be concise and avoid making unnecessarily complex decisions; simplify whenever possible
- Always verify that the result meets the objective proposed in the task
- Follow SOLID principles
- Follow Clean Code guidelines

Prefer:
- small focused components
- explicit responsibilities
- low coupling
- high cohesion
- predictable control flow
- descriptive naming
- composition over complexity

Avoid:
- god classes
- hidden side effects
- mixed responsibilities
- unnecessary abstractions
- overly generic designs
- sync-over-async patterns
