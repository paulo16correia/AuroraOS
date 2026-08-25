namespace Aurora.Core.Contracts;

/// <summary>
/// Who owns the state (LAW-005).
/// </summary>
/// <remarks>
/// LAW-005's control requires the domain model to carry a tenant. Aurora is single-tenant by
/// construction — loopback transport, one OS user, one principal — so there is exactly one, and
/// <see cref="Local"/> is it.
/// <para>
/// It is carried rather than assumed, and that is the whole reason this type exists. The law's
/// justification is that orphan state is the mechanism by which agent systems become impossible to
/// debug or erase; a tenant that is implicit is a tenant nobody can filter or delete by. Present
/// and constant, multi-tenancy is a data change. Absent, it is a redesign.
/// </para>
/// </remarks>
public static class Tenant
{
    /// <summary>The one owner of this instance's state.</summary>
    public const string Local = "tenant/local";

    public static bool IsKnown(string tenantId) =>
        string.Equals(tenantId, Local, StringComparison.Ordinal);
}
