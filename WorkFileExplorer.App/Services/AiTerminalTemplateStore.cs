using System.Text.Json;
using WorkFileExplorer.App.Helpers;
using WorkFileExplorer.App.Models;

namespace WorkFileExplorer.App.Services;

public sealed class AiTerminalTemplateStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private readonly string _filePath;

    public AiTerminalTemplateStore()
    {
        AppPaths.EnsureCreated();
        _filePath = Path.Combine(AppPaths.RootDirectory, "ai_terminal_templates.json");
    }

    public async Task<IReadOnlyList<AiTerminalTemplate>> LoadAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            if (!File.Exists(_filePath))
            {
                return CreateDefaultTemplates();
            }

            await using var stream = File.OpenRead(_filePath);
            var templates = await JsonSerializer.DeserializeAsync<List<AiTerminalTemplate>>(stream, JsonOptions, cancellationToken);
            if (templates is null || templates.Count == 0)
            {
                return CreateDefaultTemplates();
            }

            return templates
                .Where(item => !string.IsNullOrWhiteSpace(item.Name) && !string.IsNullOrWhiteSpace(item.Command))
                .ToArray();
        }
        catch
        {
            return CreateDefaultTemplates();
        }
    }

    public async Task SaveAsync(IEnumerable<AiTerminalTemplate> templates, CancellationToken cancellationToken = default)
    {
        var sanitized = templates
            .Where(item => !string.IsNullOrWhiteSpace(item.Name) && !string.IsNullOrWhiteSpace(item.Command))
            .Select(item => new AiTerminalTemplate
            {
                Name = item.Name.Trim(),
                Alias = item.Alias.Trim(),
                Command = item.Command.Trim(),
                Arguments = item.Arguments ?? string.Empty,
                DefaultPrompt = item.DefaultPrompt ?? string.Empty,
                AutoRespondEnabled = item.AutoRespondEnabled,
                AutoNumberResponse = string.IsNullOrWhiteSpace(item.AutoNumberResponse) ? "2" : item.AutoNumberResponse.Trim(),
                AutoYesNoResponse = string.IsNullOrWhiteSpace(item.AutoYesNoResponse) ? "y" : item.AutoYesNoResponse.Trim()
            })
            .ToArray();

        await using var stream = File.Create(_filePath);
        await JsonSerializer.SerializeAsync(stream, sanitized, JsonOptions, cancellationToken);
    }

    private static IReadOnlyList<AiTerminalTemplate> CreateDefaultTemplates()
    {
        return
        [
            new AiTerminalTemplate
            {
                Name = "ChatGPT CLI",
                Alias = "gpt",
                Command = "codex",
                Arguments = "\"{prompt}\"",
                DefaultPrompt = "현재 폴더 코드 리뷰해줘"
            },
            new AiTerminalTemplate
            {
                Name = "Claude CLI",
                Alias = "claude",
                Command = "claude",
                Arguments = "\"{prompt}\"",
                DefaultPrompt = "현재 폴더 버그 수정해줘"
            }
        ];
    }
}
