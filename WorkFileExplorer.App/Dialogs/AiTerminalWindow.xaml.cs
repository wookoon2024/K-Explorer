using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Input;
using WorkFileExplorer.App.Models;
using WorkFileExplorer.App.Services;

namespace WorkFileExplorer.App.Dialogs;

public partial class AiTerminalWindow : Window
{
    private static readonly Regex NumberedOptionRegex = new(@"^\s*[1-9][\)\.\:]\s+", RegexOptions.Compiled);
    private static readonly Regex NumberPromptRegex = new(@"(select|choose|option|choice|번호|선택|입력)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex YesNoPromptRegex = new(@"(y\/n|yes\/no|\[y\/n\]|\(y\/n\)|continue|proceed|계속|진행)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex PressEnterRegex = new(@"(press\s+enter|엔터)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly ObservableCollection<AiTerminalTemplate> _templates = [];
    private readonly AiTerminalTemplateStore _store = new();
    private Process? _process;
    private int _recentNumberedOptionLines;
    private DateTime _lastAutoResponseAt = DateTime.MinValue;
    private string _lastAutoResponseKey = string.Empty;

    public AiTerminalWindow(string initialWorkingDirectory)
    {
        InitializeComponent();
        TemplateComboBox.ItemsSource = _templates;
        WorkingDirectoryTextBox.Text = string.IsNullOrWhiteSpace(initialWorkingDirectory)
            ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
            : initialWorkingDirectory;
        Loaded += OnLoaded;
        Closing += OnWindowClosing;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        var loaded = await _store.LoadAsync();
        _templates.Clear();
        foreach (var item in loaded)
        {
            _templates.Add(item);
        }

        if (_templates.Count == 0)
        {
            _templates.Add(new AiTerminalTemplate
            {
                Name = "기본 템플릿",
                Alias = "run",
                Command = "cmd",
                Arguments = "/k echo {prompt}"
            });
        }

        TemplateComboBox.SelectedIndex = 0;
        AppendOutput("AI 터미널 준비 완료");
    }

    private async void OnSaveTemplateClick(object sender, RoutedEventArgs e)
    {
        var template = ReadTemplateFromEditor();
        if (template is null)
        {
            MessageBox.Show(this, "템플릿 이름과 실행 파일을 입력해 주세요.", "AI 터미널", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (TemplateComboBox.SelectedItem is AiTerminalTemplate selected)
        {
            selected.Name = template.Name;
            selected.Alias = template.Alias;
            selected.Command = template.Command;
            selected.Arguments = template.Arguments;
            selected.DefaultPrompt = template.DefaultPrompt;
            selected.AutoRespondEnabled = template.AutoRespondEnabled;
            selected.AutoNumberResponse = template.AutoNumberResponse;
            selected.AutoYesNoResponse = template.AutoYesNoResponse;
            TemplateComboBox.Items.Refresh();
        }
        else
        {
            _templates.Add(template);
            TemplateComboBox.SelectedItem = template;
        }

        await _store.SaveAsync(_templates);
        AppendOutput("[info] 템플릿 저장 완료");
    }

    private async void OnDeleteTemplateClick(object sender, RoutedEventArgs e)
    {
        if (TemplateComboBox.SelectedItem is not AiTerminalTemplate selected)
        {
            return;
        }

        _templates.Remove(selected);
        if (_templates.Count > 0)
        {
            TemplateComboBox.SelectedIndex = 0;
        }

        await _store.SaveAsync(_templates);
        AppendOutput("[info] 템플릿 삭제 완료");
    }

    private void OnNewTemplateClick(object sender, RoutedEventArgs e)
    {
        var template = new AiTerminalTemplate
        {
            Name = $"새 템플릿 {_templates.Count + 1}",
            Alias = $"t{_templates.Count + 1}",
            Command = "cmd",
            Arguments = "/k echo {prompt}",
            DefaultPrompt = "명령 실행"
        };

        _templates.Add(template);
        TemplateComboBox.SelectedItem = template;
    }

    private void OnTemplateSelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (TemplateComboBox.SelectedItem is not AiTerminalTemplate template)
        {
            return;
        }

        ApplyTemplateToEditor(template);
    }

    private void OnRunClick(object sender, RoutedEventArgs e)
    {
        if (_process is not null)
        {
            AppendOutput("[warn] 이미 실행 중입니다.");
            return;
        }

        var template = ReadTemplateFromEditor();
        if (template is null)
        {
            AppendOutput("[error] 실행 파일을 확인해 주세요.");
            return;
        }

        var workingDirectory = ResolveWorkingDirectory();
        var prompt = PromptTextBox.Text ?? string.Empty;
        var command = ReplaceTokens(template.Command, prompt, workingDirectory);
        var arguments = ReplaceTokens(template.Arguments, prompt, workingDirectory);
        if (!template.Arguments.Contains("{prompt}", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(prompt))
        {
            arguments = string.IsNullOrWhiteSpace(arguments)
                ? Quote(prompt)
                : $"{arguments} {Quote(prompt)}";
        }

        var psi = new ProcessStartInfo
        {
            FileName = command,
            Arguments = arguments,
            WorkingDirectory = workingDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        try
        {
            _recentNumberedOptionLines = 0;
            _lastAutoResponseAt = DateTime.MinValue;
            _lastAutoResponseKey = string.Empty;
            _process = new Process { StartInfo = psi, EnableRaisingEvents = true };
            _process.OutputDataReceived += OnProcessOutputDataReceived;
            _process.ErrorDataReceived += OnProcessErrorDataReceived;
            _process.Exited += OnProcessExited;
            if (!_process.Start())
            {
                AppendOutput("[error] 프로세스를 시작하지 못했습니다.");
                _process = null;
                return;
            }

            _process.BeginOutputReadLine();
            _process.BeginErrorReadLine();
            AppendOutput($"$ {command} {arguments}".TrimEnd());
        }
        catch (Exception ex)
        {
            AppendOutput($"[error] 실행 실패: {ex.Message}");
            _process = null;
        }
    }

    private void OnStopClick(object sender, RoutedEventArgs e)
    {
        StopRunningProcess();
    }

    private void OnSendInputClick(object sender, RoutedEventArgs e)
    {
        SendManualInput();
    }

    private void OnInputTextBoxKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
        {
            return;
        }

        SendManualInput();
        e.Handled = true;
    }

    private void OnRunAliasClick(object sender, RoutedEventArgs e)
    {
        var raw = (AliasCommandTextBox.Text ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(raw))
        {
            AppendOutput("[warn] 별칭 명령을 입력해 주세요.");
            return;
        }

        var firstSpace = raw.IndexOf(' ');
        var alias = firstSpace < 0 ? raw : raw[..firstSpace];
        var prompt = firstSpace < 0 ? string.Empty : raw[(firstSpace + 1)..].Trim();
        alias = alias.TrimStart('/');

        var matched = _templates.FirstOrDefault(item =>
            string.Equals(item.Alias, alias, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(item.Name, alias, StringComparison.OrdinalIgnoreCase));
        if (matched is null)
        {
            AppendOutput($"[warn] 별칭을 찾지 못했습니다: {alias}");
            return;
        }

        TemplateComboBox.SelectedItem = matched;
        if (!string.IsNullOrWhiteSpace(prompt))
        {
            PromptTextBox.Text = prompt;
        }

        OnRunClick(sender, e);
    }

    private void OnProcessOutputDataReceived(object sender, DataReceivedEventArgs e)
    {
        if (e.Data is null)
        {
            return;
        }

        AppendOutput(e.Data);
        TryAutoRespond(e.Data);
    }

    private void OnProcessErrorDataReceived(object sender, DataReceivedEventArgs e)
    {
        if (e.Data is null)
        {
            return;
        }

        AppendOutput($"[err] {e.Data}");
        TryAutoRespond(e.Data);
    }

    private void OnProcessExited(object? sender, EventArgs e)
    {
        Dispatcher.Invoke(() =>
        {
            AppendOutput("[info] 프로세스 종료");
            _process?.Dispose();
            _process = null;
        });
    }

    private void TryAutoRespond(string line)
    {
        if (AutoRespondCheckBox.IsChecked != true || _process is null)
        {
            return;
        }

        var trimmed = line.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return;
        }

        if (NumberedOptionRegex.IsMatch(trimmed))
        {
            _recentNumberedOptionLines = Math.Min(_recentNumberedOptionLines + 1, 8);
            return;
        }

        if (_recentNumberedOptionLines > 0)
        {
            _recentNumberedOptionLines--;
        }

        var shouldSendNumber = NumberPromptRegex.IsMatch(trimmed) && _recentNumberedOptionLines >= 1;
        var shouldSendYesNo = YesNoPromptRegex.IsMatch(trimmed);
        var shouldSendEnter = PressEnterRegex.IsMatch(trimmed);
        if (!shouldSendNumber && !shouldSendYesNo && !shouldSendEnter)
        {
            return;
        }

        var response = shouldSendNumber
            ? (AutoNumberTextBox.Text ?? "2").Trim()
            : shouldSendYesNo
                ? (AutoYesNoTextBox.Text ?? "y").Trim()
                : string.Empty;
        var key = $"{trimmed}|{response}";
        if (string.Equals(_lastAutoResponseKey, key, StringComparison.Ordinal) &&
            DateTime.UtcNow - _lastAutoResponseAt < TimeSpan.FromSeconds(2))
        {
            return;
        }

        _lastAutoResponseKey = key;
        _lastAutoResponseAt = DateTime.UtcNow;
        SendInputCore(response, isAuto: true);
    }

    private void SendManualInput()
    {
        var text = InputTextBox.Text ?? string.Empty;
        SendInputCore(text, isAuto: false);
        InputTextBox.Clear();
    }

    private void SendInputCore(string input, bool isAuto)
    {
        if (_process is null)
        {
            AppendOutput("[warn] 실행 중인 프로세스가 없습니다.");
            return;
        }

        try
        {
            _process.StandardInput.WriteLine(input);
            _process.StandardInput.Flush();
            AppendOutput(isAuto ? $"[auto] > {input}" : $"> {input}");
        }
        catch (Exception ex)
        {
            AppendOutput($"[error] 입력 전송 실패: {ex.Message}");
        }
    }

    private void StopRunningProcess()
    {
        if (_process is null)
        {
            return;
        }

        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex)
        {
            AppendOutput($"[error] 중지 실패: {ex.Message}");
        }
    }

    private void AppendOutput(string line)
    {
        Dispatcher.Invoke(() =>
        {
            OutputTextBox.AppendText($"[{DateTime.Now:HH:mm:ss}] {line}{Environment.NewLine}");
            OutputTextBox.ScrollToEnd();
        });
    }

    private static string Quote(string value)
    {
        return "\"" + value.Replace("\"", "\\\"") + "\"";
    }

    private static string ReplaceTokens(string text, string prompt, string workingDirectory)
    {
        return (text ?? string.Empty)
            .Replace("{prompt}", prompt, StringComparison.OrdinalIgnoreCase)
            .Replace("{cwd}", workingDirectory, StringComparison.OrdinalIgnoreCase);
    }

    private string ResolveWorkingDirectory()
    {
        var path = (WorkingDirectoryTextBox.Text ?? string.Empty).Trim();
        if (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
        {
            return path;
        }

        return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    }

    private void ApplyTemplateToEditor(AiTerminalTemplate template)
    {
        AliasTextBox.Text = template.Alias;
        CommandTextBox.Text = template.Command;
        ArgumentsTextBox.Text = template.Arguments;
        PromptTextBox.Text = template.DefaultPrompt;
        AutoRespondCheckBox.IsChecked = template.AutoRespondEnabled;
        AutoNumberTextBox.Text = template.AutoNumberResponse;
        AutoYesNoTextBox.Text = template.AutoYesNoResponse;
    }

    private AiTerminalTemplate? ReadTemplateFromEditor()
    {
        var command = (CommandTextBox.Text ?? string.Empty).Trim();
        var name = TemplateComboBox.SelectedItem is AiTerminalTemplate selectedName
            ? selectedName.Name
            : "사용자 템플릿";
        if (string.IsNullOrWhiteSpace(command))
        {
            return null;
        }

        return new AiTerminalTemplate
        {
            Name = name,
            Alias = (AliasTextBox.Text ?? string.Empty).Trim(),
            Command = command,
            Arguments = (ArgumentsTextBox.Text ?? string.Empty).Trim(),
            DefaultPrompt = PromptTextBox.Text ?? string.Empty,
            AutoRespondEnabled = AutoRespondCheckBox.IsChecked == true,
            AutoNumberResponse = string.IsNullOrWhiteSpace(AutoNumberTextBox.Text) ? "2" : AutoNumberTextBox.Text.Trim(),
            AutoYesNoResponse = string.IsNullOrWhiteSpace(AutoYesNoTextBox.Text) ? "y" : AutoYesNoTextBox.Text.Trim()
        };
    }

    private async void OnWindowClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        StopRunningProcess();
        try
        {
            await _store.SaveAsync(_templates);
        }
        catch
        {
        }
    }
}
