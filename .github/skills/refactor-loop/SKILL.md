---
name: refactor-loop
description: 'Use for the full refactor cycle: identify code to change, write behavioral tests first, make the change, run tests, fix all failures before marking done. Triggers: refactor, extract helper, reduce duplication, consolidate, move to shared, clean up repeated code.'
argument-hint: 'Describe the duplication or refactoring target, its location, and the destination (e.g., uk.osric.common).'
---

# Refactor Loop

Use this skill to safely carry a refactoring from identification through verified completion. The loop enforces the test-first discipline required by AGENTS.md and does not exit until all tests pass.

## When To Use
- Extracting duplicated logic into a shared helper.
- Moving a type from a feature module to a shared library.
- Consolidating parallel implementations into a common base.
- Any structural change that must not change observable behavior.

## Do Not Use
- Feature additions or behavioral changes — those need separate planning.
- Large architectural redesigns that span many modules at once.
- Any task that requires `uk.osric.slow` unless the user explicitly includes it.

## Repository Guardrails
- Treat [AGENTS.md](../../../../AGENTS.md) as canonical policy.
- Shared non-web helpers go in `uk.osric.common`.
- Shared web-specific helpers go in `uk.osric.common.web`.
- Never widen accessibility (e.g. `internal` → `public`) without a clear reason.
- When adding to `uk.osric.common`, check that the project reference chain remains valid (feature modules → common, not the reverse).

## Dependency Rules
The following direction is enforced by architecture tests:
```
uk.osric.web → feature modules → uk.osric.common.web → uk.osric.common
```
Do not introduce cycles. If a helper is needed by a project that currently does not reference `uk.osric.common`, add the reference explicitly and verify it does not introduce a cycle.

## The Loop — Step by Step

### 1. Scope
- Identify every callsite of the code to be extracted.
- Note the exact method signature(s) and any variations across callsites.
- Record which test files (if any) already cover the code.

### 2. Write Behavioral Tests First
- Use the `write-behavioural-tests` skill to add tests in `uk.osric.fast` that lock the current behavior.
- Run `dotnet test tests/uk.osric.fast` — all tests must pass before proceeding.
- If a test reveals a latent bug, document it but do not fix it as part of this refactor.

### 3. Create the Shared Helper
- Add the new method/class in the correct shared project (`uk.osric.common` or `uk.osric.common.web`).
- Use the tightest practical access modifier (`internal` if only used within the project, `public` if shared across assemblies).
- Seal implementation classes unless inheritance is explicitly required.
- Mirror the exact behavior of the original code — no new logic at this step.

### 4. Update Callsites
- Replace every callsite with a call to the new shared helper.
- Remove the original private/local copy.
- Add missing `using` or `<ProjectReference>` entries as needed.

### 5. Build and Test
```bash
dotnet build --configuration Release
dotnet test tests/uk.osric.fast
```
- All tests must pass before the task is considered complete.
- If tests fail, fix the root cause — do not comment out or skip tests.
- Do not mark the task done until the full loop completes green.

### 6. Commit
- Commit with a message that explains the _why_: e.g. `refactor: extract SqliteConnectionString helper to uk.osric.common`.
- Include the `Co-authored-by` trailer.

### 7. Update TODO.md
- Mark the completed item with `~~...~~ ✅ Done`.

## Common Failure Modes
| Symptom | Fix |
|---------|-----|
| Build fails due to missing reference | Add `<ProjectReference>` in the consuming `.csproj` |
| Tests fail after moving code | Check that the moved code is semantically identical (no accidental behavior change) |
| Cycle detected by architecture tests | The shared helper belongs in a lower-level project |
| Tests were never written | Return to Step 2 before proceeding |

## References
- [write-behavioural-tests skill](../write-behavioural-tests/SKILL.md)
- [AGENTS.md](../../../../AGENTS.md)
- [Verification checklist](../tidying/references/verification-checklist.md)
