---
name: tidying
description: 'Use for tidying and modernizing this repository''s C# code in phased batches. Triggers: tidy code, modernize code, raise coding standards, clean up classes, tighten visibility, add safety checks, update AGENTS guidance after verified lessons. Encodes repo-specific guardrails, exclusions, verification gates, and retrospective steps.'
argument-hint: 'Describe the target scope, modernization category, and whether slow tests are included.'
---

# Tidying

Use this skill when the task is to tidy, modernize, or standardize code in this repository without changing behavior unless that change is explicitly requested and covered by tests.

## When To Use
- Tidy a project or folder to current repository standards.
- Modernize C# syntax where the change is low-risk and improves clarity.
- Tighten visibility, sealing, and immutability rules.
- Build a backlog of standards drift before making changes.
- Review a completed modernization batch and feed durable standards updates back into AGENTS.md.

## Do Not Use
- Feature work that primarily changes runtime behavior.
- Performance investigations or bug hunts with unclear scope.
- Large refactors that require architectural redesign.
- Any task that needs `uk.osric.slow` unless the user explicitly includes it.

## Repository Guardrails
- Treat [AGENTS.md](AGENTS.md) as canonical standards policy.
- Use [.editorconfig](.editorconfig), [Directory.Build.props](Directory.Build.props), and [Directory.Build.targets](Directory.Build.targets) as enforcement context.
- Prefer K&R braces, file-scoped namespaces, modern C# where it clearly improves safety, and the tightest practical permissions.
- Push validation to system boundaries, not deep into private implementation methods.
- Before changing production behavior, add or update fast tests to lock current behavior.
- Ignore `tests/uk.osric.slow` unless the user explicitly requests it.

## Default Exclusions
- Generated files.
- `archive/`.
- EF entities, MVC model-binding types, and public contract-sensitive types unless the task explicitly approves those changes.
- Broad analyzer severity escalation unless the current phase is specifically about policy hardening.

## Procedure
1. Confirm the target scope, modernization category, and whether slow tests are in scope.
2. Read the canonical guidance in [AGENTS.md](AGENTS.md) and relevant build/style files before changing code.
3. Build a candidate file list and classify each item as safe, review-required, or defer.
4. If a production-code change could alter behavior, add or update fast tests first.
5. Apply a narrow batch of changes in one modernization category where possible.
6. Run the verification gates from [verification-checklist.md](.github/skills/tidying/references/verification-checklist.md).
7. Review the batch, capture lessons learned, and update [AGENTS.md](AGENTS.md) only when an adjustment is verified and durable.

## Safe-By-Default Modernizations
- Seal non-inheritable implementation classes.
- Tighten visibility where widening is not required.
- Normalize straightforward syntax to existing repository style.
- Convert pure DTO or helper types only when the risk rules in [risk-matrix.md](.github/skills/tidying/references/risk-matrix.md) mark them safe.

## References
- [Verification checklist](.github/skills/tidying/references/verification-checklist.md)
- [Risk matrix](.github/skills/tidying/references/risk-matrix.md)