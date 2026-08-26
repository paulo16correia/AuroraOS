namespace Aurora.Core.Contracts;

/// <summary>
/// Marks a port method that changes something outside Aurora's own reasoning.
/// </summary>
/// <remarks>
/// The policy engine decides from what a capability's descriptor says about itself: a descriptor
/// reading <c>Risk.Low</c>, no effects and no approval is allowed automatically, with no consent
/// path at all. Nothing checked that the claim was true, so a capability that wrote files and
/// declared itself effect-free would have been permitted without anybody being asked.
/// <para>
/// This is what makes the claim checkable. A port method that mutates carries the effect it
/// causes, and <c>CapabilityDeclarationTests</c> fails any capability that calls one without
/// declaring it. Put it on the method, not the interface — <see cref="INoteStore"/> both reads and
/// writes, and marking the whole interface would make honest read-only capabilities fail.
/// </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Method)]
public sealed class EffectAttribute : Attribute
{
    public EffectAttribute(string effect) => Effect = effect;

    /// <summary>The effect class, as a descriptor must declare it — for example <c>files.write</c>.</summary>
    public string Effect { get; }
}
