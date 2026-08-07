# Architecture Decision Records (ADRs)

Follow the [Michael Nygard ADR format](https://github.com/joelparkerhenderson/architecture-decision-record). Tone: precise and systematic, with proper technical depth.

## Template

```markdown
# ADR-[Number]: [Short Title of Decision]

**Status**: [Proposed | Accepted | Deprecated | Superseded by ADR-XXX]
**Date**: YYYY-MM-DD
**Deciders**: [List key people involved]

## Context

[What forces are at play? Technical, organizational, political? What needs must be met?]

## Decision

[What's the change we're proposing/have agreed to?]

## Consequences

**Positive:**

- [What becomes easier or better?]

**Negative:**

- [What becomes harder or worse?]
- [What tradeoffs are we accepting?]

**Neutral:**

- [What changes but is neither better nor worse?]

## Alternatives Considered

**Option 1**: [Brief description]

- Pros: [Why this could work]
- Cons: [Why we didn't choose it]

## References

- [Links to related docs, RFCs, benchmarks]
```

## Best practices

- One decision per ADR — keep focused.
- Immutable once accepted — new context = new ADR.
- Include metrics/data that informed the decision.
- Reference: [ADR GitHub organization](https://adr.github.io/)

## Related architecture documentation

- System design documents with visual diagram references.
- Performance benchmarks with methodology.
- Security considerations with threat models.
