namespace ShiftSoftware.ShiftEntity.Model.Dtos;

/// <summary>
/// Member-less marker for a view/upsert DTO whose entity opts into the attention feature
/// (i.e. the entity implements <c>IHasAttention</c> / <c>IHasIndexedAttention</c>).
/// <para>
/// <c>ShiftEntityForm</c> auto-detects this marker and only then fetches signals from
/// <c>GET {key}/attention</c>. A form whose DTO does not implement it never probes the endpoint,
/// so non-opted-in entities cause no request (and no 404). This is the form-side counterpart to
/// <see cref="IHasAttentionSummary"/>, which <c>ShiftList</c> uses to wire the attention column.
/// </para>
/// <para>
/// It carries no members on purpose: the form reads full signal details from the endpoint, so the
/// DTO only needs to advertise capability. Add it to the view/upsert DTO of every entity that
/// implements <c>IHasAttention</c> — forgetting it silently disables the attention banner on that
/// form.
/// </para>
/// </summary>
public interface IHasAttentionSignals
{
}
