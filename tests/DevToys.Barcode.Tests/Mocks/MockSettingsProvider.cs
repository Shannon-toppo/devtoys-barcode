using DevToys.Api;

namespace DevToys.Barcode.Tests.Mocks;

/// <summary>An in-memory <see cref="ISettingsProvider"/>.</summary>
internal sealed class MockSettingsProvider : ISettingsProvider
{
    private readonly Dictionary<string, object?> _settings = new();

    public event EventHandler<SettingChangedEventArgs>? SettingChanged;

    public int WriteCount { get; private set; }

    public T GetSetting<T>(SettingDefinition<T> settingDefinition)
        => _settings.TryGetValue(settingDefinition.Name, out object? value) ? (T)value! : settingDefinition.DefaultValue;

    public void SetSetting<T>(SettingDefinition<T> settingDefinition, T value)
    {
        WriteCount++;
        _settings[settingDefinition.Name] = value;
        SettingChanged?.Invoke(this, new SettingChangedEventArgs(settingDefinition.Name, value));
    }

    public void ResetSetting<T>(SettingDefinition<T> settingDefinition)
    {
        _settings.Remove(settingDefinition.Name);
        SettingChanged?.Invoke(this, new SettingChangedEventArgs(settingDefinition.Name, settingDefinition.DefaultValue));
    }
}
