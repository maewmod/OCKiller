using System.Text;

namespace OCKiller.Services;

/// <summary>
/// Appends activity to a log file outside the UI, so the window can stay minimal.
/// The file lives in %LOCALAPPDATA%\OCKiller\logs and is rotated at 5 MB.
/// </summary>
public sealed class AppLog
{
    private const long MaxBytes = 5 * 1024 * 1024;
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);
    private readonly object _gate = new();

    public AppLog()
    {
        var folder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "OCKiller",
            "logs");

        Directory.CreateDirectory(folder);
        LogPath = Path.Combine(folder, "ockiller.log");
    }

    public string LogPath { get; }

    public void Info(string message) => Write("INFO", message);

    public void Warn(string message) => Write("WARN", message);

    public void Success(string message) => Write("OK", message);

    public void Error(string message) => Write("ERROR", message);

    public void Write(string level, string message)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{level}] {message}{Environment.NewLine}";

        lock (_gate)
        {
            try
            {
                Rotate();
                File.AppendAllText(LogPath, line, Utf8);
            }
            catch
            {
                // Logging must never break the app.
            }
        }
    }

    private void Rotate()
    {
        var file = new FileInfo(LogPath);
        if (!file.Exists || file.Length < MaxBytes)
        {
            return;
        }

        File.Move(LogPath, LogPath + ".old", overwrite: true);
    }
}
