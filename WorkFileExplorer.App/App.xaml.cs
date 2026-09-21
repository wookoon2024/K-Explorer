using System;
using System.Windows;
using WorkFileExplorer.App.Helpers;
using WorkFileExplorer.App.Services;
using WorkFileExplorer.App.Services.Interfaces;
using WorkFileExplorer.App.ViewModels;

namespace WorkFileExplorer.App;

public partial class App : Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        LiveTrace.Init();
        LiveTrace.Write("App startup");
        DispatcherDiag.Init(Dispatcher);

        DispatcherUnhandledException += (_, args) =>
        {
            LiveTrace.Write($"DispatcherUnhandledException: {args.Exception}");
        };

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            LiveTrace.Write($"DomainUnhandledException: {args.ExceptionObject}");
        };

        ISettingsStorageService settingsStorage = new SettingsStorageService();
        IUsageTrackingService usageTracking = new UsageTrackingService();
        IFileSystemService fileSystem = new FileSystemService();
        IQuickAccessService quickAccess = new QuickAccessService();
        IPathHistoryStoreService pathHistoryStore = new PathHistoryStoreService();
        Models.AppSettings startupSettings;
        try
        {
            startupSettings = await settingsStorage.LoadSettingsAsync();
        }
        catch
        {
            startupSettings = new Models.AppSettings();
        }

        ApplyUiFont(startupSettings.FileListFontFamily);

        var viewModel = new MainWindowViewModel(fileSystem, settingsStorage, usageTracking, quickAccess, pathHistoryStore);
        var window = new MainWindow
        {
            DataContext = viewModel
        };
        ApplyWindowPlacement(window, startupSettings);
        Exit += (_, _) =>
        {
            try
            {
                CaptureWindowPlacement(window, viewModel);
                viewModel.SaveSessionStateAsync().GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                LiveTrace.Write($"SaveSessionState on exit failed: {ex}");
            }
        };

        LiveTrace.Write("MainWindow created");
        window.Show();
        LiveTrace.Write("MainWindow shown");
        await viewModel.InitializeAsync();
        LiveTrace.Write("InitializeAsync complete");
    }

    /// <summary>
    /// The file list font doubles as the application font. WPF resolves the default control font
    /// through the message-font resource key, so replacing it re-fonts every window and dialog
    /// that is created afterwards; the main window also binds its FontFamily directly.
    /// </summary>
    public static void ApplyUiFont(string? fontFamily)
    {
        try
        {
            var value = MainWindowViewModel.ResolveFileListFontFamily(fontFamily);
            // A font shipped inside the assembly is addressed as "./Assets/Fonts/#Family". The path
            // is relative, so a code-created FontFamily needs the pack base to resolve it; without
            // the base WPF silently falls back to the default font.
            var resolved = value.Contains('#')
                ? new System.Windows.Media.FontFamily(new Uri("pack://application:,,,/"), value)
                : new System.Windows.Media.FontFamily(value);

            // Windows and their controls reference this key directly; the system font keys are
            // also set so any theme style that reads them stays consistent.
            Current.Resources["UiFontFamily"] = resolved;
            Current.Resources[SystemFonts.MessageFontFamilyKey] = resolved;
            Current.Resources[SystemFonts.MenuFontFamilyKey] = resolved;
        }
        catch
        {
        }
    }

    private static void ApplyWindowPlacement(Window window, Models.AppSettings settings)
    {
        var hasPosition = IsFinite(settings.WindowLeft) && IsFinite(settings.WindowTop);
        var hasSize = IsFinite(settings.WindowWidth) && settings.WindowWidth > 0 &&
                      IsFinite(settings.WindowHeight) && settings.WindowHeight > 0;
        if (hasPosition && hasSize)
        {
            var virtualLeft = SystemParameters.VirtualScreenLeft;
            var virtualTop = SystemParameters.VirtualScreenTop;
            var virtualWidth = SystemParameters.VirtualScreenWidth;
            var virtualHeight = SystemParameters.VirtualScreenHeight;

            var width = Math.Max(window.MinWidth, Math.Min(settings.WindowWidth, virtualWidth));
            var height = Math.Max(window.MinHeight, Math.Min(settings.WindowHeight, virtualHeight));
            var left = Math.Clamp(settings.WindowLeft, virtualLeft, virtualLeft + virtualWidth - width);
            var top = Math.Clamp(settings.WindowTop, virtualTop, virtualTop + virtualHeight - height);

            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Left = left;
            window.Top = top;
            window.Width = width;
            window.Height = height;
        }

        if (settings.WindowMaximized)
        {
            window.WindowState = WindowState.Maximized;
        }
    }

    private static void CaptureWindowPlacement(Window window, MainWindowViewModel viewModel)
    {
        var isMaximized = window.WindowState == WindowState.Maximized;
        var bounds = isMaximized ? window.RestoreBounds : new Rect(window.Left, window.Top, window.Width, window.Height);
        if (!IsFinite(bounds.Left) || !IsFinite(bounds.Top) || !IsFinite(bounds.Width) || !IsFinite(bounds.Height) ||
            bounds.Width <= 0 || bounds.Height <= 0)
        {
            return;
        }

        viewModel.UpdateWindowPlacement(bounds.Left, bounds.Top, bounds.Width, bounds.Height, isMaximized);
    }

    private static bool IsFinite(double value)
    {
        return !(double.IsNaN(value) || double.IsInfinity(value));
    }
}
