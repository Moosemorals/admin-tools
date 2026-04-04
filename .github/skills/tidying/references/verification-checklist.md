# Verification Checklist

Run this checklist after each tidying batch.

## Required Gates
1. Build the solution in Release configuration.
2. Run `dotnet test tests/uk.osric.fast/uk.osric.fast.csproj` for any batch that touches production or fast-test code.
3. Run slow tests only when the user explicitly includes them or the changed area requires them.
4. Confirm no unapproved public API shape changes were introduced.
5. Keep each batch focused enough for review and rollback.

## Retrospective Gate
1. Review the phase plan and batch outcome.
2. Capture lessons learned.
3. Update AGENTS.md only for confirmed, reusable standards adjustments.
4. Update the tidying skill when the workflow itself needs to change.

## Notes
- Configuration-only changes such as skill files or documentation do not require fast tests unless they alter build or runtime behavior.
- For production behavior changes, tests come before code changes.