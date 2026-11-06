using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;

namespace OCKiller.Services;

public sealed record WarpSnapshot(
    string? Ip,
    bool Managed,
    bool Missing,
    string? Proxy,
    string? Message);

/// <summary>
/// Drives Cloudflare WARP through warp-cli, mirroring the steps of the
/// "Reset usage" button: read IP, disconnect, new registration, connect, read IP again.
/// </summary>
public sealed class WarpService
{
    private const string TraceUrl = "https://www.cloudflare.com/cdn-cgi/trace";
    private readonly AppLog _log;

    public WarpService(AppLog log)
    {
        _log = log;
        CliPath = ResolveCliPath();
    }

    public string CliPath { get; }

    public static string ResolveCliPath()
    {
        var overridePath = Environment.GetEnvironmentVariable("WARP_CLI");
        if (!string.IsNullOrWhiteSpace(overridePath) && File.Exists(overridePath))
        {
            return overridePath;
        }

        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var known = Path.Combine(programFiles, "Cloudflare", "Cloudflare WARP", "warp-cli.exe");
        if (File.Exists(known))
        {
            return known;
        }

        foreach (var folder in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                     .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(folder.Trim('"'), "warp-cli.exe");
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
            catch
            {
                // Ignore malformed PATH entries.
            }
        }

        return known;
    }

    /// <summary>Detects WARP, proxy mode, managed state and the current egress IP.</summary>
    public async Task<WarpSnapshot> InspectAsync(CancellationToken cancellationToken)
    {
        _log.Info("Looking for Cloudflare WARP...");

        if (!File.Exists(CliPath))
        {
            _log.Error($"warp-cli not found at {CliPath}");
            return new WarpSnapshot(null, false, true, null, "warp-cli not found");
        }

        _log.Info($"Found WARP: {CliPath}");

        string? proxy = null;
        try
        {
            var json = await RunAsync(cancellationToken, "-j", "settings");
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var settings = root.TryGetProperty("settings", out var nested) ? nested : root;

            var mode = settings.TryGetProperty("operation_mode", out var modeElement)
                ? modeElement.GetString()
                : null;

            if (string.Equals(mode, "proxy", StringComparison.OrdinalIgnoreCase))
            {
                var port = settings.TryGetProperty("proxy_port", out var portElement) &&
                           portElement.TryGetInt32(out var parsedPort)
                    ? parsedPort
                    : 40000;

                proxy = $"http://127.0.0.1:{port}";
                _log.Info($"WARP is in proxy mode: {proxy}");
            }
            else
            {
                _log.Info("WARP is in full tunnel mode");
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.Warn($"Could not read settings: {ex.Message}");
        }

        cancellationToken.ThrowIfCancellationRequested();

        var managed = false;
        try
        {
            var json = await RunAsync(cancellationToken, "-j", "registration", "show");
            using var document = JsonDocument.Parse(json);
            managed = document.RootElement.TryGetProperty("managed", out var managedElement) &&
                      managedElement.ValueKind == JsonValueKind.True;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.Warn($"Could not read registration: {ex.Message}");
        }

        if (managed)
        {
            _log.Warn("This machine is managed by Cloudflare Zero Trust (managed)");
        }

        cancellationToken.ThrowIfCancellationRequested();

        _log.Info("Reading current IP...");
        var ip = await GetEgressIpAsync(proxy, cancellationToken);
        _log.Success($"Current IP: {ip}");

        return new WarpSnapshot(ip, managed, false, proxy, null);
    }

    /// <summary>Performs the full registration reset and returns the new egress IP.</summary>
    public async Task<string> ResetAsync(
        string beforeIp,
        string? proxy,
        CancellationToken cancellationToken)
    {
        _log.Info("--- Starting reset ---");

        _log.Info("1/4 Disconnect");
        await RunAsync(cancellationToken, "disconnect");

        _log.Info("2/4 Delete old registration");
        await RunAsync(cancellationToken, "registration", "delete");

        _log.Info("3/4 Create new registration");
        await RunAsync(cancellationToken, "registration", "new");

        _log.Info("4/4 Connect");
        await RunAsync(cancellationToken, "connect");

        _log.Info("Waiting for a new IP (up to 30 seconds)...");

        var deadline = DateTime.UtcNow.AddSeconds(30);
        string? afterIp = null;

        while (DateTime.UtcNow <= deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                afterIp = await GetEgressIpAsync(proxy, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                afterIp = null;
            }

            if (afterIp is not null && !string.Equals(afterIp, beforeIp, StringComparison.Ordinal))
            {
                break;
            }

            await Task.Delay(500, cancellationToken);
        }

        if (afterIp is null)
        {
            throw new InvalidOperationException("Connected, but the new IP could not be read.");
        }

        if (string.Equals(afterIp, beforeIp, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The IP is still the same. Wait a moment and try again.");
        }

        _log.Success($"New IP: {afterIp}");
        _log.Success("Reset complete - you can reopen opencode now");
        return afterIp;
    }

    private async Task<string> RunAsync(CancellationToken cancellationToken, params string[] arguments)
    {
        var allArguments = new List<string> { "--accept-tos", "--no-ansi" };
        allArguments.AddRange(arguments);

        var (exitCode, stdout, stderr) = await RunProcessAsync(CliPath, allArguments, cancellationToken);

        if (exitCode != 0)
        {
            var detail = string.IsNullOrWhiteSpace(stderr) ? stdout.Trim() : stderr.Trim();
            var message = $"warp-cli {string.Join(' ', arguments)} failed (exit {exitCode}): {detail}";
            _log.Error(message);
            throw new InvalidOperationException(message);
        }

        return stdout.Trim();
    }

    private static async Task<string> GetEgressIpAsync(string? proxy, CancellationToken cancellationToken)
    {
        using var handler = new HttpClientHandler();
        if (proxy is not null)
        {
            handler.Proxy = new WebProxy(proxy);
            handler.UseProxy = true;
        }

        using var client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(8),
        };

        var content = await client.GetStringAsync(TraceUrl, cancellationToken);
        var line = content
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault(entry => entry.StartsWith("ip=", StringComparison.Ordinal));

        if (line is null)
        {
            throw new InvalidOperationException("The trace endpoint did not return an IP address.");
        }

        return line[3..].Trim();
    }

    private static async Task<(int ExitCode, string StdOut, string StdErr)> RunProcessAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo(fileName)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };

        try
        {
            if (!process.Start())
            {
                throw new InvalidOperationException("The process did not start.");
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException($"Could not start {Path.GetFileName(fileName)}: {ex.Message}", ex);
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        using (cancellationToken.Register(() =>
               {
                   try
                   {
                       if (!process.HasExited)
                       {
                           process.Kill(entireProcessTree: true);
                       }
                   }
                   catch
                   {
                       // Process already exited.
                   }
               }))
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }

        var stdout = await stdoutTask.ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);
        return (process.ExitCode, stdout, stderr);
    }
}
