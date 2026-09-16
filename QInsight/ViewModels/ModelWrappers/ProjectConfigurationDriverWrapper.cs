using System.Text;
using System.Collections.ObjectModel;
using Qenex.QInsight.Helpers;
using Qenex.QLibs.QUI;
using Qenex.QSuite.Drivers.Driver;

namespace Qenex.QInsight.ViewModels.ModelWrappers;

public class ProjectConfigurationDriverWrapper(IDriverBase driver, bool isNew = false) : PropertyChangedBase
{
    private const string FileDataLoggerDriverName = "FileDataLoggerDriver";
    private const string FileDataReplayDriverName = "FileDataReplayDriver";
    private const string FileDataReplayDisplayLabel = "File data replay";
    private const string ReplayFileSettingKey = "file";
    private bool originalIsEnabled = driver.IsEnabled;
    private bool isEnabled = driver.IsEnabled;
    private string originalLabel = driver.Label;
    private string label = driver.Label;
    // The replay driver's "file" setting is written by the Import button, not by the operator,
    // so the editor shows the settings without it and the wrapper keeps the value aside to put
    // it back on apply. Other drivers show their RawSettings unchanged.
    private string hiddenReplayFile = IsFileDataReplay(driver)
        ? SplitReplayFileSetting(driver.RawSettings).File
        : string.Empty;
    private string originalSettings = ToDisplayedSettings(driver);
    private string settings = ToDisplayedSettings(driver);

    public IDriverBase Driver => driver;
    public bool IsNew => isNew;
    public ObservableCollection<ProjectConfigurationLoadedProtocolWrapper> Protocols { get; } = new(
        driver.Protocols.Select(protocol => new ProjectConfigurationLoadedProtocolWrapper(protocol)));

    public string Name => driver.Specification.Name;
    public string Version => driver.Specification.Version.ToDisplayString();

    public string Label
    {
        get
        {
            if (IsFileDataReplayDriver)
            {
                return FileDataReplayDisplayLabel;
            }

            return label;
        }
        set
        {
            if (!CanEditLabel)
            {
                return;
            }

            value ??= string.Empty;
            if (label == value)
            {
                return;
            }

            label = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasChanges));
        }
    }

    public string ToolTip
    {
        get
        {
            var sb = new StringBuilder();
            sb.Append("Driver:");
            sb.Append(Environment.NewLine);
            sb.Append($"Name\t{driver.Specification.Name}");
            sb.Append(Environment.NewLine);
            sb.Append($"Label\t{driver.Specification.Label}");
            sb.Append(Environment.NewLine);
            sb.Append($"Desc.\t{driver.Specification.Description}");
            sb.Append(Environment.NewLine);
            sb.Append($"Version\t{driver.Specification.Version.ToDisplayString()}");
            sb.Append(Environment.NewLine);
            sb.Append($"Author\t{driver.Specification.Author}");
            sb.Append(Environment.NewLine);
            sb.Append($"Co.\t{driver.Specification.Company}");

            return sb.ToString();
        }
    }

    public bool IsEnabled
    {
        get => isEnabled;
        set
        {
            if (!CanEditIsEnabled || isEnabled == value)
            {
                return;
            }

            isEnabled = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasChanges));
        }
    }

    public bool CanEditIsEnabled =>
        !IsFileDataReplayDriver;

    public bool CanEditLabel =>
        !IsFileDataLoggerDriver && !IsFileDataReplayDriver;

    public string Settings
    {
        get => settings;
        set
        {
            value ??= string.Empty;
            if (settings == value)
            {
                return;
            }

            settings = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasChanges));
        }
    }

    public bool HasChanges => label != originalLabel
                              || (CanEditIsEnabled && isEnabled != originalIsEnabled)
                              || settings != originalSettings;

    public void ApplyChanges()
    {
        if (label != originalLabel)
        {
            driver.Label = label;
            originalLabel = label;
        }

        if (CanEditIsEnabled)
        {
            driver.IsEnabled = isEnabled;
            originalIsEnabled = isEnabled;
        }

        if (settings != originalSettings)
        {
            var displayedSettings = settings;
            var rawSettings = settings;
            if (IsFileDataReplayDriver)
            {
                // A "file" typed into the editor by hand (tests, headless projects) wins over the
                // hidden one; either way the stored text carries the file and the editor does not.
                var (typedFile, rest) = SplitReplayFileSetting(settings);
                if (!string.IsNullOrWhiteSpace(typedFile))
                {
                    hiddenReplayFile = typedFile;
                }

                displayedSettings = rest;
                rawSettings = ComposeReplayRawSettings(hiddenReplayFile, rest);
            }

            var previousSettings = driver.RawSettings;
            driver.RawSettings = rawSettings;
            try
            {
                driver.SetConfiguration();
            }
            catch
            {
                driver.RawSettings = previousSettings;
                driver.SetConfiguration();
                throw;
            }

            originalSettings = displayedSettings;
            if (settings != displayedSettings)
            {
                settings = displayedSettings;
                OnPropertyChanged(nameof(Settings));
            }
        }

        OnPropertyChanged(nameof(HasChanges));
    }

    public void CancelChanges()
    {
        if (label != originalLabel)
        {
            label = originalLabel;
            OnPropertyChanged(nameof(Label));
        }

        if (CanEditIsEnabled && isEnabled != originalIsEnabled)
        {
            isEnabled = originalIsEnabled;
            OnPropertyChanged(nameof(IsEnabled));
        }

        if (settings != originalSettings)
        {
            settings = originalSettings;
            OnPropertyChanged(nameof(Settings));
        }

        OnPropertyChanged(nameof(HasChanges));
    }

    private bool IsFileDataReplayDriver => IsFileDataReplay(driver);

    private bool IsFileDataLoggerDriver =>
        driver.Specification.Name.Equals(FileDataLoggerDriverName, StringComparison.OrdinalIgnoreCase);

    private static bool IsFileDataReplay(IDriverBase driver) =>
        driver.Specification.Name.Equals(FileDataReplayDriverName, StringComparison.OrdinalIgnoreCase);

    private static string ToDisplayedSettings(IDriverBase driver) =>
        IsFileDataReplay(driver)
            ? SplitReplayFileSetting(driver.RawSettings).Remaining
            : driver.RawSettings;

    // Splits "key=value;key=value" text into the value of the "file" entry (last one wins, as in
    // SettingsParser) and the text with every "file" entry removed. Only ';' separates entries,
    // so a value may contain '\' and ':' freely; the other entries are kept verbatim.
    private static (string File, string Remaining) SplitReplayFileSetting(string rawSettings)
    {
        var file = string.Empty;
        var rest = new List<string>();
        foreach (var entry in (rawSettings ?? string.Empty).Split(';'))
        {
            var pair = entry.Split('=', 2);
            if (pair.Length == 2 && pair[0].Trim().Equals(ReplayFileSettingKey, StringComparison.OrdinalIgnoreCase))
            {
                file = pair[1].Trim();
                continue;
            }

            rest.Add(entry);
        }

        return (file, string.Join(';', rest).Trim(';'));
    }

    private static string ComposeReplayRawSettings(string file, string rest)
    {
        if (string.IsNullOrWhiteSpace(file))
        {
            return rest;
        }

        return string.IsNullOrWhiteSpace(rest)
            ? $"{ReplayFileSettingKey}={file}"
            : $"{ReplayFileSettingKey}={file};{rest}";
    }
}
