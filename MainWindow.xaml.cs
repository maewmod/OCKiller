using System.Diagnostics;
using Microsoft.UI;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using OCKiller.Services;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics;
using Windows.UI;

namespace OCKiller;

public sealed partial class MainWindow : Window
{
    private static readonly SolidColorBrush SecondaryBrush = new(Color.FromArgb(255, 0x8B, 0x93, 0xA3));
    private static readonly SolidColorBrush SuccessBrush = new(Color.FromArgb(255, 0x3F, 0xB9, 0x50));
    private static readonly SolidColorBrush WarningBrush = new(Color.FromArgb(255, 0xD2, 0x99, 0x22));
    private static readonly SolidColorBrush ErrorBrush = new(Color.FromArgb(255, 0xF8, 0x51, 0x49));

    private readonly AppLog _log = new();
    private readonly WarpService _warp;

    private CancellationTokenSource? _cancellation;
    private bool _started;
    private bool _busy;
    private bool _ready;
    private bool _managed;
    private bool _missing;
    private string? _ip;

    public MainWindow()
    {
        InitializeComponent();

        _warp = new WarpService(_log);
        LoadTitleBarLogo();
        ConfigureWindow();

        _log.Info("=== OCKiller started ===");

        RootGrid.Loaded += OnRootLoaded;
        Closed += OnClosed;
    }

    private void LoadTitleBarLogo()
    {
        using var stream = typeof(MainWindow).Assembly
            .GetManifestResourceStream("OCKiller.Assets.app.png");

        if (stream is null)
        {
            return;
        }

        var bitmap = new BitmapImage();
        bitmap.SetSource(stream.AsRandomAccessStream());
        TitleBarLogo.Source = bitmap;
    }

    private void ConfigureWindow()
    {
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        try
        {
            SystemBackdrop = new MicaBackdrop { Kind = MicaKind.BaseAlt };
        }
        catch
        {
            // Mica is unavailable (older Windows); the dark card background is enough.
        }

        var titleBar = AppWindow.TitleBar;
        titleBar.ButtonBackgroundColor = Colors.Transparent;
        titleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
        titleBar.ButtonForegroundColor = Color.FromArgb(255, 0xE7, 0xE9, 0xEE);
        titleBar.ButtonInactiveForegroundColor = Color.FromArgb(255, 0x6E, 0x76, 0x81);
        titleBar.ButtonHoverBackgroundColor = Color.FromArgb(40, 255, 255, 255);
        titleBar.ButtonHoverForegroundColor = Colors.White;
        titleBar.ButtonPressedBackgroundColor = Color.FromArgb(60, 255, 255, 255);

        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.PreferredMinimumWidth = 540;
            presenter.PreferredMinimumHeight = 420;
        }

        ResizeAndCenter();
    }

    private void ResizeAndCenter()
    {
        const int preferredWidth = 620;
        const int preferredHeight = 460;

        var workArea = DisplayArea
            .GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary)
            .WorkArea;

        var width = Math.Min(preferredWidth, workArea.Width - 80);
        var height = Math.Min(preferredHeight, workArea.Height - 80);

        AppWindow.Resize(new SizeInt32(width, height));
        AppWindow.Move(new PointInt32(
            workArea.X + ((workArea.Width - width) / 2),
            workArea.Y + ((workArea.Height - height) / 2)));
    }

    private async void OnRootLoaded(object sender, RoutedEventArgs e)
    {
        if (_started)
        {
            return;
        }

        _started = true;
        await RunAsync(JobMode.Refresh);
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        _log.Info("=== OCKiller closed ===");

        try
        {
            _cancellation?.Cancel();
        }
        catch
        {
            // Nothing to cancel.
        }

        _cancellation?.Dispose();
    }

    private async Task RunAsync(JobMode mode)
    {
        if (_busy)
        {
            return;
        }

        _busy = true;
        _ready = false;
        _missing = false;

        SetStatus("Checking...", StatusKind.Secondary);
        SetHint($"WARP: {_warp.CliPath}");
        UpdateButtons();

        _cancellation?.Dispose();
        _cancellation = new CancellationTokenSource();
        var cancellationToken = _cancellation.Token;

        _log.Info(mode == JobMode.Reset ? "Reset requested" : "Refresh requested");

        try
        {
            var snapshot = await _warp.InspectAsync(cancellationToken);

            _managed = snapshot.Managed;
            _missing = snapshot.Missing;

            if (snapshot.Ip is not null)
            {
                SetIp(snapshot.Ip);
            }

            if (snapshot.Missing)
            {
                SetIp(null);
                SetStatus("Cloudflare WARP was not found on this machine", StatusKind.Warning);
                SetHint("Install WARP, then click Refresh IP");
                return;
            }

            _ready = true;

            if (mode == JobMode.Refresh)
            {
                if (snapshot.Managed)
                {
                    SetStatus("This machine is managed by Zero Trust - reset unavailable", StatusKind.Warning);
                    SetHint("Managed devices cannot be reset");
                }
                else
                {
                    SetStatus("Ready to reset", StatusKind.Secondary);
                    SetHint($"WARP: {_warp.CliPath}");
                }

                return;
            }

            if (snapshot.Managed)
            {
                SetStatus("This machine is managed by Cloudflare Zero Trust and cannot be reset", StatusKind.Warning);
                SetHint("Managed devices cannot be reset");
                return;
            }

            var afterIp = await _warp.ResetAsync(snapshot.Ip!, snapshot.Proxy, cancellationToken);

            SetIp(afterIp);
            SetStatus($"IP changed: {snapshot.Ip} → {afterIp}", StatusKind.Success);
            SetHint("Reset complete - reopen opencode now");
        }
        catch (OperationCanceledException)
        {
            _log.Warn("Operation cancelled");
            SetStatus("Cancelled", StatusKind.Warning);
            SetHint("Stopped while running");
        }
        catch (Exception ex)
        {
            _log.Error(ex.Message);
            SetStatus(ex.Message, StatusKind.Error);
            SetHint("See the log for details");
        }
        finally
        {
            _busy = false;
            _ready = _ip is not null && !_missing;
            UpdateButtons();
        }
    }

    private void SetIp(string? ip)
    {
        _ip = ip;
        IpText.Text = string.IsNullOrWhiteSpace(ip) ? "-" : ip;
        CopyButton.IsEnabled = !_busy && !string.IsNullOrWhiteSpace(ip);
    }

    private void SetStatus(string text, StatusKind kind)
    {
        StatusText.Text = text;

        var brush = kind switch
        {
            StatusKind.Success => SuccessBrush,
            StatusKind.Warning => WarningBrush,
            StatusKind.Error => ErrorBrush,
            _ => SecondaryBrush,
        };

        StatusText.Foreground = brush;
        StatusIcon.Foreground = brush;
        StatusIcon.Glyph = kind switch
        {
            StatusKind.Success => "\uE930",
            StatusKind.Warning => "\uE7BA",
            StatusKind.Error => "\uEA39",
            _ => "\uE946",
        };
    }

    private void SetHint(string text) => HintText.Text = text;

    private void UpdateButtons()
    {
        ResetButton.IsEnabled = !_busy && _ready && !_managed && !_missing;
        RefreshButton.IsEnabled = !_busy;
        CopyButton.IsEnabled = !_busy && !string.IsNullOrWhiteSpace(_ip);
        InstallButton.Visibility = _missing ? Visibility.Visible : Visibility.Collapsed;
        Progress.Visibility = _busy ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void ResetButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy)
        {
            return;
        }

        if (_managed)
        {
            await ShowMessageAsync(
                "Reset unavailable",
                "This machine is managed by Cloudflare Zero Trust, so it cannot be reset.");
            return;
        }

        var content = new StackPanel { Spacing = 8 };
        content.Children.Add(new TextBlock
        {
            Text = "Resetting deletes the Cloudflare WARP registration and creates a new one.",
            TextWrapping = TextWrapping.Wrap,
        });
        content.Children.Add(new TextBlock
        {
            Text = "The IP address for the whole device will change (not just opencode).",
            TextWrapping = TextWrapping.Wrap,
        });
        content.Children.Add(new TextBlock
        {
            Text = "If opencode is running, close it first and reopen it after the reset.",
            TextWrapping = TextWrapping.Wrap,
        });
        content.Children.Add(new TextBlock
        {
            Text = "Continue?",
            TextWrapping = TextWrapping.Wrap,
        });

        var dialog = new ContentDialog
        {
            XamlRoot = RootGrid.XamlRoot,
            Title = "Confirm reset",
            Content = content,
            PrimaryButtonText = "Reset",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };

        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary)
        {
            await RunAsync(JobMode.Reset);
        }
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        await RunAsync(JobMode.Refresh);
    }

    private void CopyButton_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_ip))
        {
            return;
        }

        try
        {
            var package = new DataPackage();
            package.SetText(_ip);
            Clipboard.SetContent(package);
            Clipboard.Flush();
            SetHint("IP copied");
        }
        catch
        {
            SetHint("Copy failed");
        }
    }

    private async void InstallButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await Windows.System.Launcher.LaunchUriAsync(new Uri("https://1.1.1.1/"));
        }
        catch
        {
            SetHint("Could not open the download page");
        }
    }

    private void OpenLogButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo("notepad.exe", $"\"{_log.LogPath}\"")
            {
                UseShellExecute = true,
            });
        }
        catch
        {
            SetHint(_log.LogPath);
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private async Task ShowMessageAsync(string title, string message)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = RootGrid.XamlRoot,
            Title = title,
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
            CloseButtonText = "OK",
            DefaultButton = ContentDialogButton.Close,
        };

        await dialog.ShowAsync();
    }

    private enum JobMode
    {
        Refresh,
        Reset,
    }

    private enum StatusKind
    {
        Secondary,
        Success,
        Warning,
        Error,
    }
}
