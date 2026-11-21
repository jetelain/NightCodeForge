namespace NightCodeForge.Installer.ViewModels.Wizard;

using CommunityToolkit.Mvvm.ComponentModel;
using NightCodeForge.Installer.Core.Models;

/// <summary>
/// Live-updatable checklist item representing a prerequisite step (Step 2) or deployed component (Step 3).
/// </summary>
public sealed partial class WizardChecklistItemViewModel : ObservableObject
{
    public WizardChecklistItemViewModel(
        string key,
        string title,
        ProvisioningStatus status,
        string detail)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentException.ThrowIfNullOrWhiteSpace(title);

        Key = key;
        Title = title;
        Status = status;
        Detail = detail ?? string.Empty;
    }

    public string Key { get; }

    [ObservableProperty]
    public partial string Title { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusGlyph), nameof(StatusLabel), nameof(IsChecked))]
    public partial ProvisioningStatus Status { get; set; }

    [ObservableProperty]
    public partial string Detail { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusLabel))]
    public partial string? StatusLabelOverride { get; set; }

    public bool IsChecked => Status is ProvisioningStatus.Completed or ProvisioningStatus.Verified;

    public string StatusGlyph =>
        Status switch
        {
            ProvisioningStatus.Completed or ProvisioningStatus.Verified => "✓",
            ProvisioningStatus.InProgress => "⟳",
            ProvisioningStatus.RebootRequired => "⚠",
            ProvisioningStatus.Failed => "✕",
            ProvisioningStatus.Skipped => "–",
            _ => "○"
        };

    public string StatusLabel =>
        !string.IsNullOrWhiteSpace(StatusLabelOverride)
            ? StatusLabelOverride
            : Status switch
            {
                ProvisioningStatus.NotStarted => "Pending",
                ProvisioningStatus.InProgress => "In Progress",
                ProvisioningStatus.Verified => "Verified",
                ProvisioningStatus.Completed => "Installed",
                ProvisioningStatus.RebootRequired => "Reboot Required",
                ProvisioningStatus.Failed => "Failed",
                ProvisioningStatus.Skipped => "Skipped",
                _ => "Pending"
            };

    public void Update(ProvisioningStatus status, string detail, string? statusLabelOverride = null)
    {
        Status = status;
        Detail = detail ?? string.Empty;
        StatusLabelOverride = statusLabelOverride;
    }
}
