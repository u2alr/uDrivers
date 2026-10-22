using uDrivers.Models;
using Microsoft.UI.Xaml;

namespace uDrivers.ViewModels;

/// <summary>Small helpers for x:Bind function bindings.</summary>
public static class Bind
{
    public static bool Not(bool value) => !value;
    public static Visibility VisibleIf(bool value) => value ? Visibility.Visible : Visibility.Collapsed;
    public static Visibility CollapsedIf(bool value) => value ? Visibility.Collapsed : Visibility.Visible;
    public static Visibility VisibleIfEmpty(int count, bool hasChecked) =>
        hasChecked && count == 0 ? Visibility.Visible : Visibility.Collapsed;
    public static Visibility VisibleIfAny(int count) => count > 0 ? Visibility.Visible : Visibility.Collapsed;
    public static Visibility VisibleIfText(string? text) =>
        string.IsNullOrWhiteSpace(text) ? Visibility.Collapsed : Visibility.Visible;

    public static string LastChecked(DateTime? when) =>
        when is null ? "Not checked yet" : $"Last checked {when:MMM d, h:mm tt}";

    public static string StatusGlyph(UpdateStatus status, UpdateKind kind) => status switch
    {
        UpdateStatus.Installed => "",   // checkmark
        UpdateStatus.Failed => "",      // error
        UpdateStatus.Downloading => "", // download
        UpdateStatus.Installing => "",  // sync
        _ when kind == UpdateKind.Bios => "",     // chipset
        _ when kind == UpdateKind.Firmware => "", // chip
        _ => "",                                  // device
    };

    public static bool CanToggle(UpdateStatus status, bool canInstall) =>
        canInstall && (status == UpdateStatus.Available || status == UpdateStatus.Failed);

    public static string StatusLine(string statusText, string? note) =>
        statusText.Length > 0 ? statusText : note ?? "";
}
