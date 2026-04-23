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

    private static void ApplyWindowPlacement(Window window, Models.AppSettings settings)
    {
        var hasPosition = IsFinite(settings.WindowLeft) && IsFinite(settings.WindowTop);
        var hasSize = IsFinite(settings.WindowWidth) && settings.WindowWidth > 0 &&
                      IsFinite(settings.WindowHeight) && settings.WindowHeight > 0;
        if (!hasPosition || !hasSize)
        {
            return;
        }

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
