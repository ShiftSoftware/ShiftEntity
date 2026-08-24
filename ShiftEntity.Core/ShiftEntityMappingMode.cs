namespace ShiftSoftware.ShiftEntity.Core;

/// <summary>
/// Which mapper a <c>ShiftRepository</c> resolves when the repository itself configured none — i.e. no
/// <c>UseMapper</c>, no <c>UseGeneratedMapper</c>, no DI registration for the triple.
/// <para>
/// This exists so "use the source-generated mapper" is a one-line, reversible, per-service decision instead of
/// an edit to every repository. Wiring the registry in unconditionally was evaluated and rejected: it is
/// exactly the change that silently swaps a hand-tuned AutoMapper profile for convention output. Measured on
/// the first triple examined during the audit, the generated mapper dropped two members and mapped
/// <c>DateTime? → DateTimeOffset?</c> with the server's local offset where the profile pinned UTC — three
/// regressions, zero warnings. A mode makes that swap a deliberate, reviewable act.
/// </para>
/// </summary>
public enum ShiftEntityMappingMode
{
    /// <summary>
    /// Today's behaviour, and the default: an explicit mapper, else DI, else AutoMapper. The registry is not
    /// consulted, so a source-generated mapper can exist and go unused. Nothing changes by upgrading.
    /// </summary>
    AutoMapperFirst = 0,

    /// <summary>
    /// Prefer the source-generated mapper from <see cref="ShiftEntityMapperRegistry"/>, but still fall back to
    /// AutoMapper for triples the registry does not cover. The migration setting: flip a service to this, clear
    /// the diagnostics, run the parity differ, then move to <see cref="GeneratedOnly"/>.
    /// </summary>
    GeneratedFirst = 1,

    /// <summary>
    /// The registry or nothing. A triple with no generated mapper and no explicit one gets no mapper at all, so
    /// its mapping methods throw. This is where "an explicit mapper is required" is actually true — and it is
    /// what startup validation hard-fails on, so the failure lands at boot with a complete list rather than
    /// per-request in production.
    /// </summary>
    GeneratedOnly = 2,
}
