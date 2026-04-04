# Risk Matrix

Use this matrix before making tidying changes.

## Safe
- Sealing internal implementation classes with no intended inheritance.
- Tightening obvious `private` or `internal` visibility where references prove it is safe.
- Syntax normalization that does not change semantics.

## Review Required
- Public class sealing in shared libraries.
- `init` or record conversions for DTOs used by serialization, reflection, or external callers.
- Validation refactors that could change exception type or timing.
- Test-structure changes that could affect discovery or naming.

## Defer Unless Explicitly Approved
- EF entity shape changes.
- MVC or Razor model-binding shape changes.
- Changes to generated files.
- Broad analyzer or warning policy escalation across the whole solution.