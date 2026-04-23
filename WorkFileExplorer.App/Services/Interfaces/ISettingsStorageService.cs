using WorkFileExplorer.App.Models;

namespace WorkFileExplorer.App.Services.Interfaces;

public interface ISettingsStorageService
{
    Task<AppSettings> LoadSettingsAsync(CancellationToken cancellationToken = default);
    Task SaveSettingsAsync(AppSettings settings, CancellationToken cancellationToken = default);
}