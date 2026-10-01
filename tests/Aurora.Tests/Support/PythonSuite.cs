using System.Diagnostics;
using Xunit;

namespace Aurora.Tests.Support;

/// <summary>
/// Runs one of a plugin's own Python test modules, and surfaces its output when it fails.
/// </summary>
/// <remarks>
/// The plugins are Python and their rules are tested where they live, so the .NET suite runs those
/// modules rather than restating them. This is the harness for it, in one place because it was in
/// three and all three had the same bug.
/// <para>
/// <b>Both pipes are drained at once, and that is not a detail.</b> Reading stdout to the end and
/// then stderr deadlocks: <c>unittest -v</c> writes its progress to stderr, and a child blocked
/// writing into a full stderr pipe never exits, so the parent waits on a stdout end-of-file that
/// cannot arrive. Windows sizes a pipe at 4KB, which about twenty verbose test names fill, and the
/// whole .NET suite hung there. macOS and Linux give 64KB and hid it for as long as the suite only
/// ran on a Mac. It is the same mistake <c>ServicePluginTests</c> has a test for in the production
/// host, arrived at from the other direction.
/// </para>
/// </remarks>
public static class PythonSuite
{
    /// <summary>How long a module gets before it is treated as hung.</summary>
    /// <remarks>
    /// Generous — some of these stand up a real socket and drive a real handshake. It exists so
    /// that a module which never finishes fails with its output, rather than stopping the run.
    /// </remarks>
    private static readonly TimeSpan Limit = TimeSpan.FromMinutes(3);

    /// <summary>The repository root, found by walking up to the folder holding the plugins.</summary>
    public static DirectoryInfo Repository()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "plugins")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return directory!;
    }

    /// <param name="workingDirectory">The plugin folder holding the module.</param>
    /// <param name="module">The test module, as <c>unittest</c> names it.</param>
    /// <param name="expected">
    /// How many tests the module must run. Asserted so a module that stops being collected fails
    /// here rather than passing with nothing run.
    /// </param>
    public static string Run(string workingDirectory, string module, int expected)
    {
        using var python = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "python3",
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            },
        };

        python.StartInfo.ArgumentList.Add("-m");
        python.StartInfo.ArgumentList.Add("unittest");
        python.StartInfo.ArgumentList.Add(module);
        python.StartInfo.ArgumentList.Add("-v");

        python.Start();

        // Started before either is awaited, so neither pipe can fill while the other is being read.
        Task<string> outputs = python.StandardOutput.ReadToEndAsync();
        Task<string> errors = python.StandardError.ReadToEndAsync();

        if (!python.WaitForExit((int)Limit.TotalMilliseconds))
        {
            python.Kill(entireProcessTree: true);
            python.WaitForExit();

            Assert.Fail(
                $"{module} did not finish within {Limit.TotalMinutes:F0} minutes. "
                + $"What it had said:{Environment.NewLine}{Read(outputs)}{Read(errors)}");
        }

        var output = Read(outputs) + Read(errors);

        Assert.True(python.ExitCode == 0, output);
        Assert.Contains($"Ran {expected} test", output, StringComparison.Ordinal);

        return output;
    }

    /// <summary>
    /// Runs a short expression against the plugin's own modules and returns what it printed.
    /// </summary>
    /// <remarks>
    /// For asking the program a question rather than running its tests — what a module actually
    /// handles, say, which is a better source than a list written beside it.
    /// </remarks>
    public static string Evaluate(string workingDirectory, string expression)
    {
        using var python = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "python3",
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            },
        };

        python.StartInfo.ArgumentList.Add("-c");
        python.StartInfo.ArgumentList.Add(expression);

        python.Start();

        // Both at once, for the same reason as above: a traceback is written to stderr, and it is
        // exactly the case where reading stdout first would hang instead of failing.
        Task<string> outputs = python.StandardOutput.ReadToEndAsync();
        Task<string> errors = python.StandardError.ReadToEndAsync();

        if (!python.WaitForExit((int)TimeSpan.FromSeconds(60).TotalMilliseconds))
        {
            python.Kill(entireProcessTree: true);
            python.WaitForExit();
            Assert.Fail($"python did not finish: {Read(outputs)}{Read(errors)}");
        }

        Assert.True(python.ExitCode == 0, Read(errors));

        return Read(outputs);
    }

    /// <summary>What a pipe held, without letting a stuck read become a stuck suite.</summary>
    private static string Read(Task<string> pipe) =>
        pipe.Wait(TimeSpan.FromSeconds(10)) ? pipe.Result : string.Empty;
}
