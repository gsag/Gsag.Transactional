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

# Git Commit Rules

When creating commits:
- Follow Conventional Commits, e.g. `chore: add sample project scaffold`
- Include a concise body describing what changed and why
- End the commit message with a `Co-authored-by` trailer using the model/agent's own name, e.g.:
```text
Co-authored-by: Codex <codex@openai.com>
```

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

# Graphify Workflow

The repository architecture graph must represent the main library code only.

Scope configuration:
- `.graphifyignore` excludes `docs/`, `tests/`, and `samples/`.
- `graphify-out/` is ignored by Git and contains generated graph artifacts.
- Use `src` as the graph root; do not use the full repository root for routine updates.

Commands:

```powershell
# Build or rebuild the structural graph for the library
graphify extract src --code-only --force --no-viz --out .

# Build or update the graph after source changes
.\scripts\graphify\setup-graphify.ps1

# Regenerate the interactive visualization
graphify export html

# Measure estimated token reduction
graphify benchmark graphify-out\graph.json
```

Use `--directed` for a separate graph when tracing call flow or dependency direction. The default undirected graph is preferred for community clustering and broad architectural exploration.

Do not treat benchmark values as API billing measurements. They estimate the context required by graph traversal versus loading the corpus directly.


## New Machine Setup

Run from the repository root:

```powershell
.\scripts\graphify\setup-graphify.ps1

# Force a clean structural rebuild when needed
.\scripts\graphify\setup-graphify.ps1 -Rebuild

# Command Prompt wrapper
scripts\graphify\setup-graphify.bat
```

The setup script verifies `uv` and `graphify`, maintains `.graphifyignore`, builds the graph from `src`, generates the HTML visualization, and runs the token benchmark.
