using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Aurora.Core.Abstractions;
using Aurora.Core.Contracts;
using Aurora.Core.Cryptography;
using Aurora.Adapters.Files;

namespace Aurora.Adapters.Genomes;

/// <summary>
/// Signs and verifies genome manifests with ECDSA P-256 (RFC 036 rule 1).
/// </summary>
/// <remarks>
/// Asymmetric rather than an HMAC, because a genome is authored in one place and verified in many:
/// an installation should be able to check a manifest without holding anything that would let it
/// forge one. P-256 comes from the BCL, so this behaves identically on Windows, macOS and Linux.
/// <para>
/// A real deployment ships only the public key. The private key file here exists so that a genome
/// can be authored and tested locally.
/// </para>
/// </remarks>
public sealed class EcdsaGenomeSigner : IGenomeSigner, IDisposable
{
    private const char UnitSeparator = (char)0x1F;

    private readonly ECDsa _key;

    public EcdsaGenomeSigner(ECDsa key) => _key = key;

    /// <summary>Loads the signing key, creating one on first use with owner-only permissions.</summary>
    public static EcdsaGenomeSigner FromKeyFile(string path)
    {
        var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        if (File.Exists(path))
        {
            key.ImportPkcs8PrivateKey(File.ReadAllBytes(path), out _);
            return new EcdsaGenomeSigner(key);
        }

        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        OwnerOnly.Write(
            path, FileMode.CreateNew, stream => stream.Write(key.ExportPkcs8PrivateKey()));

        return new EcdsaGenomeSigner(key);
    }

    public string Sign(Genome unsigned)
    {
        var hash = IntegrityHashOf(unsigned);
        return Convert.ToBase64String(_key.SignData(
            System.Text.Encoding.UTF8.GetBytes(hash), HashAlgorithmName.SHA256));
    }

    public bool Verify(Genome genome)
    {
        // Both halves must hold: the hash proves the fields are unaltered, the signature proves
        // they came from the author. Checking only one would leave the other forgeable.
        if (!string.Equals(genome.IntegrityHash, IntegrityHashOf(genome), StringComparison.Ordinal))
        {
            return false;
        }

        try
        {
            return _key.VerifyData(
                System.Text.Encoding.UTF8.GetBytes(genome.IntegrityHash),
                Convert.FromBase64String(genome.Signature),
                HashAlgorithmName.SHA256);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    /// <summary>Stamps a draft with its hash and signature, ready to register.</summary>
    public Genome Seal(Genome unsigned)
    {
        var withHash = unsigned with { IntegrityHash = IntegrityHashOf(unsigned) };
        return withHash with { Signature = Sign(withHash) };
    }

    public void Dispose() => _key.Dispose();

    /// <summary>Covers every field except the hash and signature themselves.</summary>
    public static string IntegrityHashOf(Genome g) => Hashing.Sha256Hex(string.Join(
        UnitSeparator,
        new[]
        {
            g.Id, g.Family, g.Version, g.ParentGenomeRef ?? string.Empty, g.Status,
            g.ConstitutionVersion, g.LawSetVersion, g.BaseIdentityTemplateRef,
            g.PersonalityBaselineRef, g.DevelopmentProfileRef,
            g.MindSchemaVersion.ToString(CultureInfo.InvariantCulture),
            string.Join(',', g.AllowedCapabilityIds), string.Join(',', g.PolicyBundleRefs),
            string.Join(',', g.DefaultLocales), g.BootstrapConfigurationRef,
        }));
}
