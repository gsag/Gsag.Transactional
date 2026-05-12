---
_layout: landing
---

# Gsag.Transactional

Lightweight declarative `[Transactional]` attribute for C# using only native .NET — DispatchProxy + TransactionScope. No AOP libraries.

Wraps any interface method in a `System.Transactions.TransactionScope` using `DispatchProxy`, giving you Spring-style transaction management without PostSharp, Castle DynamicProxy, or any other weaving tool.

[![CI](https://github.com/gsag/Gsag.Transactional/actions/workflows/ci.yml/badge.svg)](https://github.com/gsag/Gsag.Transactional/actions/workflows/ci.yml)
[![NuGet](https://img.shields.io/nuget/v/Gsag.Transactional.Core.svg)](https://www.nuget.org/packages/Gsag.Transactional.Core)
![.NET](https://img.shields.io/badge/.NET-8%20%7C%209-512BD4)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](https://github.com/gsag/Gsag.Transactional/blob/main/LICENSE)

## Install

```bash
dotnet add package Gsag.Transactional.Core
```

## Setup

```csharp
builder.Services.AddTransactionalServices(typeof(Program).Assembly);
builder.Services.AddTransactionalLogging(); // optional — Debug + Warning via MEL
```

Mark a method on the **concrete class**:

```csharp
public class OrderService : IOrderService
{
    [Transactional]
    public async Task PlaceOrderAsync(Order order)
    {
        // runs inside a TransactionScope — commits on success, rolls back on exception
    }
}
```

## Explore

<div class="cards">

### [Installation](articles/installation.md)
NuGet setup, DI registration, and logging configuration.

### [Getting Started](articles/getting-started.md)
Your first transactional method, step by step.

### [Propagation Modes](articles/propagation.md)
Required, RequiresNew, and Suppress — when and how to use each.

### [Transaction Hooks](articles/hooks.md)
BeforeCommit, AfterCommit, AfterRollback, AfterCompletion — lifecycle callbacks.

### [Rollback Rules](articles/rollback-rules.md)
Control exactly which exceptions trigger rollback.

### [Architecture](articles/architecture.md)
How DispatchProxy, TransactionScope, and AsyncLocal fit together.

</div>
