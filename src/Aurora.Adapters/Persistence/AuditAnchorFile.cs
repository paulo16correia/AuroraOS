using System.Globalization;

namespace Aurora.Adapters.Persistence;

/// <summary>The newest audit position observed, held outside the database (docs/adr/0005).</summary>
public sealed record AuditAnchor(long Sequence, string RecordHash);

/// <summary>
/// External head anchor for the audit chain. A hash chain proves that records were not edited in
/// place, but says nothing about records that were *removed*: deleting the newest rows leaves a
/// perfectly valid shorter chain. Recording the head outside the database makes that visible.
/// </summary>
public sealed class AuditAnchorFile
{
    private readonly string _path;
    private readonly object _sync = new();

    public AuditAnchorFile(string path)
    {
        _path = path;
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }
    }

    public AuditAnchor? Read()
    {
        lock (_sync)
        {
            if (!File.Exists(_path))
            {
                return null;
            }

            var text = File.ReadAllText(_path).Trim();
            var parts = text.Split(' ', 2);
            if (parts.Length != 2
                || !long.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var sequence))
            {
                return null;
            }

            return new AuditAnchor(sequence, parts[1]);
        }
    }

    /// <summary>Advances the anchor. Never moves backwards, so a replayed write cannot erase history.</summary>
    public void Advance(long sequence, string recordHash)
    {
        lock (_sync)
        {
            AuditAnchor? current = ReadUnlocked();
            if (current is not null && current.Sequence >= sequence)
            {
                return;
            }

            var temp = _path + ".tmp";
            File.WriteAllText(temp, $"{sequence.ToString(CultureInfo.InvariantCulture)} {recordHash}");
            File.Move(temp, _path, overwrite: true);
        }
    }

    private AuditAnchor? ReadUnlocked()
    {
        if (!File.Exists(_path))
        {
            return null;
        }

        var parts = File.ReadAllText(_path).Trim().Split(' ', 2);
        return parts.Length == 2
            && long.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var sequence)
                ? new AuditAnchor(sequence, parts[1])
                : null;
    }
}
