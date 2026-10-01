namespace Aurora.Tests.Support;

/// <summary>
/// Copies a plugin's source into the folder a test will run it from.
/// </summary>
/// <remarks>
/// <para>
/// Every rig that drives a real plugin needs this, and each one used to write its own loop over
/// <c>EnumerateFiles</c> — which copies the top of the folder and nothing below it. That held for
/// as long as no top-level file needed anything in a subfolder, and stopped holding the day the
/// Discord plugin started importing its vendored copy of Aurora's speech client: the plugin ran
/// perfectly by hand and would not start under the host, because the folder it was given was not
/// the plugin.
/// </para>
/// <para>
/// So subfolders come too, except the ones that are not code. A plugin's models and binaries are
/// gigabytes and no test touches them — the tests stand up fakes — and <c>__pycache__</c> is a
/// build product of whichever interpreter wrote it.
/// </para>
/// </remarks>
public static class PluginCopy
{
    private static readonly string[] Skip = ["__pycache__", "models", "bin", "work", ".venv"];

    /// <summary>Copies <paramref name="source"/> into <paramref name="destination"/>.</summary>
    public static void Into(string source, string destination)
    {
        Directory.CreateDirectory(destination);

        foreach (var file in Directory.EnumerateFiles(source))
        {
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: true);
        }

        foreach (var folder in Directory.EnumerateDirectories(source))
        {
            var name = Path.GetFileName(folder);

            if (Skip.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            Into(folder, Path.Combine(destination, name));
        }
    }
}
