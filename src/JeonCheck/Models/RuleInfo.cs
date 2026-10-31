using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace JeonCheck.Models;

public class RuleDatabase
{
    [JsonPropertyName("rules")]
    public List<RuleInfo> Rules { get; set; } = new();
}

public class RuleInfo : INotifyPropertyChanged
{
    private bool _isEnabled = true;

    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("category")]
    public string Category { get; set; } = string.Empty;

    [JsonPropertyName("severity")]
    public string Severity { get; set; } = "Warning";

    [JsonPropertyName("obligation")]
    public string Obligation { get; set; } = "필수(Required)";

    [JsonPropertyName("defaultEnabled")]
    public bool DefaultEnabled { get; set; } = true;

    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("summary")]
    public string Summary { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;

    [JsonPropertyName("badExample")]
    public string BadExample { get; set; } = string.Empty;

    [JsonPropertyName("goodExample")]
    public string GoodExample { get; set; } = string.Empty;

    [JsonIgnore]
    public bool IsEnabled
    {
        get => _isEnabled;
        set
        {
            if (_isEnabled != value)
            {
                _isEnabled = value;
                OnPropertyChanged();
            }
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? propName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propName));
}
