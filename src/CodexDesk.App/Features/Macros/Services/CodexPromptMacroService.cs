using System.Text.Json;
using CodexDesk.Models;

namespace CodexDesk.Services;

public sealed class CodexPromptMacroService
{
    private const string PreferencesKey = "codex.prompt-macros-json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly object _sync = new();
    private List<CodexPromptMacroItem> _macros = new();

    public event Action<string>? KeyObserved;

    public IReadOnlyList<CodexPromptMacroItem> Macros
    {
        get
        {
            lock (_sync)
            {
                return _macros.ToList();
            }
        }
    }

    public void Load()
    {
        try
        {
            var json = Preferences.Default.Get(PreferencesKey, string.Empty);
            if (string.IsNullOrWhiteSpace(json))
            {
                lock (_sync)
                {
                    _macros = [];
                }

                return;
            }

            var parsed = JsonSerializer.Deserialize<List<CodexPromptMacroItem>>(json, JsonOptions);
            lock (_sync)
            {
                _macros = CodexPromptMacroRules.ReindexShortcuts(parsed ?? []);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[CodexPromptMacroService.Load] Failed: {ex}");
            lock (_sync)
            {
                _macros = [];
            }
        }
    }

    public bool TryAdd(string? name, string? text, bool autoSend, out string? errorMessage)
    {
        lock (_sync)
        {
            if (!CodexPromptMacroRules.TryCreateMacro(_macros, name, text, autoSend, out var macro, out errorMessage))
            {
                return false;
            }

            _macros.Add(macro!);
        }

        Save();
        return true;
    }

    public bool RemoveAt(int index)
    {
        lock (_sync)
        {
            if (index < 0 || index >= _macros.Count)
            {
                return false;
            }

            _macros.RemoveAt(index);
        }

        Save();
        return true;
    }

    public bool TryUpdateAt(int index, string? name, string? text, bool autoSend, out string? errorMessage)
    {
        errorMessage = null;

        var normalizedText = (text ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(normalizedText))
        {
            errorMessage = "매크로 텍스트가 비어 있습니다.";
            return false;
        }

        lock (_sync)
        {
            if (index < 0 || index >= _macros.Count)
            {
                errorMessage = "수정할 매크로를 찾을 수 없습니다.";
                return false;
            }

            var normalizedName = string.IsNullOrWhiteSpace(name)
                ? $"Prompt Macro {index + 1}"
                : name.Trim();

            _macros[index] = _macros[index] with
            {
                Name = normalizedName,
                Text = normalizedText,
                AutoSend = autoSend
            };
        }

        Save();
        return true;
    }

    public bool Move(int fromIndex, int toIndex)
    {
        lock (_sync)
        {
            if (fromIndex < 0 || fromIndex >= _macros.Count || toIndex < 0 || toIndex >= _macros.Count)
            {
                return false;
            }

            if (fromIndex == toIndex)
            {
                return true;
            }

            var moving = _macros[fromIndex];
            _macros.RemoveAt(fromIndex);
            _macros.Insert(toIndex, moving);
        }

        Save();
        return true;
    }

    public void PublishKeyObserved(string keyDebugMessage)
    {
        if (string.IsNullOrWhiteSpace(keyDebugMessage))
        {
            return;
        }

        KeyObserved?.Invoke(keyDebugMessage.Trim());
    }

    private void Save()
    {
        string json;
        lock (_sync)
        {
            _macros = CodexPromptMacroRules.ReindexShortcuts(_macros);
            json = JsonSerializer.Serialize(_macros, JsonOptions);
        }

        Preferences.Default.Set(PreferencesKey, json);
    }
}
