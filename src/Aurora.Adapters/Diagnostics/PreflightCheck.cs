namespace Aurora.Adapters.Diagnostics;

/// <summary>How a single preflight check came out.</summary>
public enum PreflightStatus
{
    /// <summary>The check confirmed the property holds.</summary>
    Pass,

    /// <summary>Something an operator should see, but not a reason to refuse the demo.</summary>
    Warn,

    /// <summary>A property that must hold does not; a plugin or Aurora will not work correctly.</summary>
    Fail,

    /// <summary>A statement of fact, neither good nor bad.</summary>
    Info,
}

/// <summary>One line of the preflight report.</summary>
/// <param name="Component">What was checked, e.g. "audit key" or "plugin/voice interpreter".</param>
/// <param name="Status">The outcome.</param>
/// <param name="Detail">
/// A human-readable explanation an operator can act on. Never carries a secret value, a token, or
/// key material — only names, paths and outcomes.
/// </param>
public sealed record PreflightCheck(string Component, PreflightStatus Status, string Detail);
