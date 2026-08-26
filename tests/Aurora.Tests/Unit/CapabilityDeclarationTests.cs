using System.Reflection;
using Aurora.Core.Abstractions;
using Aurora.Core.Contracts;
using Aurora.Server;
using Aurora.Tests.Support;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Aurora.Tests.Unit;

/// <summary>
/// A capability cannot get itself permitted by describing itself wrongly.
/// </summary>
/// <remarks>
/// The policy engine decides entirely from the descriptor: <c>Risk.Low</c> with no effects and no
/// approval is allowed automatically, with no consent path and nobody asked. That makes the
/// descriptor a security control written by whoever added the capability, and until now nothing
/// checked it. A capability that wrote to the sandbox and declared itself effect-free would have
/// run on every call.
/// <para>
/// The check is possible because the ports say what they do: a method that mutates carries
/// <see cref="EffectAttribute"/>. A capability that calls one and does not declare it fails here,
/// on the day it is written.
/// </para>
/// </remarks>
public sealed class CapabilityDeclarationTests
{
    /// <summary>Every port method that changes something, and the effect it causes.</summary>
    private static IReadOnlyList<(string Method, string Effect)> Mutations()
    {
        var found = new List<(string, string)>();

        foreach (Type port in typeof(ICapability).Assembly.GetTypes().Where(t => t.IsInterface))
        {
            foreach (MethodInfo method in port.GetMethods())
            {
                EffectAttribute? effect = method.GetCustomAttribute<EffectAttribute>();

                if (effect is not null)
                {
                    found.Add((method.Name, effect.Effect));
                }
            }
        }

        return found;
    }

    /// <summary>
    /// Aurora's own capabilities — the ones whose descriptor is written in this repository.
    /// </summary>
    /// <remarks>
    /// A plugin's capability is excluded because its descriptor comes from a manifest at runtime,
    /// so there is nothing here to read and nothing a reviewer could have got wrong. What a plugin
    /// declares is checked where it can be: the registry refuses a capability outside the manifest
    /// and the bridge raises a <c>PrivilegeEscalation</c> incident when one is attempted.
    /// </remarks>
    private static IReadOnlyList<Type> Capabilities() =>
        [.. typeof(Aurora.Adapters.Capabilities.EchoSayCapability).Assembly.GetTypes()
            .Where(t => t.IsClass && !t.IsAbstract && typeof(ICapability).IsAssignableFrom(t))
            .Where(t => t != typeof(Aurora.Adapters.Plugins.PluginCapabilityBridge))];

    [Fact]
    public void TheOnlyCapabilityThisCannotReadIsThePluginBridge()
    {
        // The exclusion above is by name, so it excludes exactly one thing. If a built-in is one
        // day written with a constructor this cannot call, it must fail here rather than quietly
        // stop being checked by the three tests below.
        var unreadable = typeof(Aurora.Adapters.Capabilities.EchoSayCapability).Assembly.GetTypes()
            .Where(t => t.IsClass && !t.IsAbstract && typeof(ICapability).IsAssignableFrom(t))
            .Where(t =>
            {
                try
                {
                    DescriptorOf(t);
                    return false;
                }
                catch (Exception unbuildable) when (unbuildable is not Xunit.Sdk.XunitException)
                {
                    return true;
                }
            })
            .Select(t => t.Name)
            .ToList();

        // The bridge is excluded on purpose; everything else must be readable, so a built-in
        // cannot quietly stop being checked by the tests below.
        Assert.Equal(["PluginCapabilityBridge"], unreadable);
    }

    private static string SourceOf(Type capability)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "src")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);

        var file = Directory
            .EnumerateFiles(
                Path.Combine(directory!.FullName, "src"), $"{capability.Name}.cs",
                SearchOption.AllDirectories)
            .FirstOrDefault();

        return file is null ? string.Empty : File.ReadAllText(file);
    }

    /// <summary>
    /// The descriptor, without building the capability's dependencies.
    /// </summary>
    /// <remarks>
    /// A capability's constructor takes the ports it needs, and one of those opens the database.
    /// Nulls are passed instead: a capability constructor assigns its fields and nothing else, and
    /// nothing here ever calls <c>ExecuteAsync</c>. If one day a constructor does real work, this
    /// throws and says so rather than silently skipping the capability.
    /// </remarks>
    private static CapabilityDescriptor DescriptorOf(Type capability)
    {
        ConstructorInfo constructor = capability
            .GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .OrderBy(c => c.GetParameters().Length)
            .First();

        var arguments = constructor.GetParameters()
            .Select(p => p.ParameterType.IsValueType
                ? Activator.CreateInstance(p.ParameterType)
                : null)
            .ToArray();

        var instance = constructor.Invoke(arguments) as ICapability;
        Assert.NotNull(instance);

        return instance!.Descriptor;
    }

    [Fact]
    public void ACapabilityThatChangesSomethingSaysSo()
    {
        var lies = new List<string>();

        foreach (Type capability in Capabilities())
        {
            var source = SourceOf(capability);

            if (source.Length == 0)
            {
                continue;
            }

            CapabilityDescriptor descriptor = DescriptorOf(capability);

            foreach ((var method, var effect) in Mutations())
            {
                if (!source.Contains($".{method}(", StringComparison.Ordinal))
                {
                    continue;
                }

                if (!descriptor.Effects.Contains(effect, StringComparer.Ordinal))
                {
                    lies.Add($"{descriptor.ActionId} calls {method} but does not declare {effect}");
                }
            }
        }

        // Declaring more than you cause is safe and this does not complain about it. Declaring
        // less is how an effectful capability gets itself allowed without a consent path.
        Assert.Empty(lies);
    }

    [Fact]
    public void OnlyACapabilityThatChangesNothingIsAllowedWithoutAsking()
    {
        var wrong = new List<string>();

        foreach (Type capability in Capabilities())
        {
            CapabilityDescriptor descriptor = DescriptorOf(capability);

            // The one shape the policy engine allows outright (policy.low_readonly).
            var automatic = descriptor is
            {
                Risk: RiskLevel.Low, ApprovalRequired: false,
            } && descriptor.Effects.Count == 0;

            if (!automatic)
            {
                continue;
            }

            var source = SourceOf(capability);

            foreach ((var method, var effect) in Mutations())
            {
                if (source.Contains($".{method}(", StringComparison.Ordinal))
                {
                    wrong.Add(
                        $"{descriptor.ActionId} is allowed automatically but calls {method} ({effect})");
                }
            }
        }

        // If this fails, a capability runs on every call with nobody asked and changes something
        // while it does. That is the widest hole the policy engine can have.
        Assert.Empty(wrong);
    }

    [Fact]
    public void EveryCapabilityDeclaresAnInputSchemaThatRefusesWhatItDoesNotUnderstand()
    {
        foreach (Type capability in Capabilities())
        {
            CapabilityDescriptor descriptor = DescriptorOf(capability);
            var schema = descriptor.InputSchema.GetRawText();

            // additionalProperties:false, always. A capability that accepts unknown fields will one
            // day be handed one that means something to a later version and nothing to this one.
            Assert.Contains("\"additionalProperties\":false", schema, StringComparison.Ordinal);
            Assert.Contains("\"type\":\"object\"", schema, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void AHighRiskCapabilityIsNotPermittedUnlessItCanBeUndone()
    {
        foreach (Type capability in Capabilities())
        {
            CapabilityDescriptor descriptor = DescriptorOf(capability);

            if (descriptor.Risk != RiskLevel.High)
            {
                continue;
            }

            // policy.high_requires_approval_and_reversibility. A HIGH capability that is not
            // reversible is not refused here — it is refused by the policy engine on every call,
            // which makes it dead on arrival. Better to say so while it is being written.
            Assert.True(
                descriptor is { ApprovalRequired: true, Reversible: true },
                $"{descriptor.ActionId} is HIGH risk, so policy will deny it unless it requires "
                + "approval and is reversible");
        }
    }

    [Fact]
    public void APluginCannotClaimAnyOfAurorasOwnActionIds()
    {
        IConfiguration config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Aurora:BearerToken"] = "a-token-long-enough-to-pass-validation",
                ["Aurora:DbPath"] = TestTemp.Path("ids") + ".db",
                ["Aurora:SandboxRoot"] = TestTemp.Folder("ids-sandbox"),
            })
            .Build();

        AuroraServerOptions options = AuroraServerOptions.FromConfiguration(config);

        IReadOnlyList<string> guarded = ServiceRegistration.BuiltInCapabilityIds(options);

        // Every built-in, not just the one with a parameterless constructor. This list is what the
        // manifest reader checks a plugin's declared ids against; when it held only echo.say, a
        // plugin could declare files.write_sandbox and pass validation.
        foreach (Type capability in Capabilities())
        {
            Assert.Contains(DescriptorOf(capability).ActionId, guarded);
        }

        Assert.Contains("files.write_sandbox", guarded);
        Assert.Contains("memory.remember", guarded);
        Assert.Contains("clock.now", guarded);
    }
}
