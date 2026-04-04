---
name: write-behavioural-tests
description: 'Use for writing behavioural tests in uk.osric.fast that lock current behavior before making production code changes. Triggers: write tests first, add tests before changing, cover before refactor, lock behavior, TDD.'
argument-hint: 'Describe what production code you need to cover and what behaviors to lock.'
---

# Write Behavioural Tests

Use this skill to add fast unit or BDD tests to `uk.osric.fast` that capture and verify current behavior before any production code is changed.

## When To Use
- Before any production code change as required by AGENTS.md.
- To lock behavior of a method, class, or module before refactoring it.
- To fill coverage gaps on code paths that are about to be touched.
- To add regression tests after a bug is identified.

## Do Not Use
- Adding tests _after_ production code changes — tests must come first.
- Tests that require a running server or browser — those belong in `uk.osric.slow`.
- Tests for generated files (EF Migrations, `.Designer.cs`).

## Repository Guardrails
- Test project: `tests/uk.osric.fast/uk.osric.fast.csproj`
- Framework: NUnit + Reqnroll (BDD `.feature` files welcome but not required for pure unit tests).
- Keep tests fast — no database, no HTTP, no file I/O unless using in-memory fakes.
- Use `[TestFixture]`, `[Test]`, `[TestCase]` for parameterised cases.
- Use Reqnroll `.feature` files for user-facing behaviors with multiple scenarios.
- Name test files `{Subject}Tests.cs` and place them in a folder matching the source project name (e.g. `tests/uk.osric.fast/Common/` for `uk.osric.common`).

## Default Exclusions
- Slow/browser tests — those go in `uk.osric.slow`.
- Tests that would require spinning up the full web host.
- Generated migration code.

## Procedure
1. Identify the exact behaviors to lock: list each method signature and the expected outputs for key inputs.
2. Check whether tests already exist in `uk.osric.fast` for the subject.
3. Create or update the test file in the appropriate subfolder of `tests/uk.osric.fast/`.
4. Write test cases for:
   - The happy path (valid input → expected output).
   - Edge cases (empty, null, boundary values).
   - Error cases (exceptions, failure returns).
5. Run `dotnet test tests/uk.osric.fast` to confirm all new tests pass **before any production change**.
6. If a test fails on existing code, treat that as a bug to document — do not mask it by changing the test.

## NUnit Patterns for This Repository

### Static Utility Tests
```csharp
[TestFixture]
public sealed class MyHelperTests {
    [Test]
    public void DoesX_WithValidInput_ReturnsY() {
        var result = MyHelper.DoX("valid");
        Assert.That(result, Is.EqualTo("expected"));
    }

    [TestCase("a", true)]
    [TestCase("",  false)]
    public void DoesX_ReturnsExpectedBool(string input, bool expected) {
        Assert.That(MyHelper.Check(input), Is.EqualTo(expected));
    }
}
```

### Behavior Locking (Property-Style)
When the exact output varies (e.g., random tokens), verify _shape_ rather than exact value:
```csharp
[Test]
public void GeneratedToken_IsUrlSafe_And_NonEmpty() {
    var token = TokenHelper.Generate();
    Assert.That(token, Is.Not.Null.And.Not.Empty);
    Assert.That(token, Does.Not.Contain("+").And.Not.Contain("/").And.Not.Contain("="));
}
```

### Extension Method Tests
```csharp
[Test]
public void ExtensionMethod_WithNull_ReturnsFalse() {
    string? input = null;
    Assert.That(input.MyExtension(), Is.False);
}
```

## References
- [AGENTS.md](../../../../AGENTS.md) — canonical testing policy
- [Verification checklist](../tidying/references/verification-checklist.md)
