namespace NightCodeForge.Installer.Converters;

using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using NightCodeForge.Installer.Core.Constants;
using NightCodeForge.Installer.Core.Models;

internal static class ConverterParameterParser
{
    internal static bool TryParseInt(object? parameter, out int value)
    {
        switch (parameter)
        {
            case int intParameter:
                value = intParameter;
                return true;
            case string stringParameter:
                return int.TryParse(stringParameter, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
            default:
                value = 0;
                return false;
        }
    }
}

public sealed class IntEqualityToVisibilityConverter : IValueConverter
{
    public static readonly IntEqualityToVisibilityConverter Instance = new();

    private IntEqualityToVisibilityConverter()
    {
    }

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is int valueInt &&
        ConverterParameterParser.TryParseInt(parameter, out var parameterInt) &&
        valueInt == parameterInt;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class IntEqualityToFontWeightConverter : IValueConverter
{
    public static readonly IntEqualityToFontWeightConverter Instance = new();

    private IntEqualityToFontWeightConverter()
    {
    }

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is int valueInt &&
        ConverterParameterParser.TryParseInt(parameter, out var parameterInt) &&
        valueInt == parameterInt
            ? FontWeight.Bold
            : FontWeight.Normal;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class UiModeToVisibilityConverter : IValueConverter
{
    public static readonly UiModeToVisibilityConverter Instance = new();

    private UiModeToVisibilityConverter()
    {
    }

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not InstallerUiMode currentMode)
        {
            return false;
        }

        return parameter switch
        {
            InstallerUiMode targetMode => currentMode == targetMode,
            string modeName => Enum.TryParse<InstallerUiMode>(modeName, ignoreCase: true, out var parsed) &&
                               currentMode == parsed,
            _ => false
        };
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class DoctorStatusToBrushConverter : IValueConverter
{
    public static readonly DoctorStatusToBrushConverter Instance = new();

    private static readonly IBrush PassBrush = new ImmutableSolidColorBrush(Color.Parse(InstallerThemeColors.SuccessHex));
    private static readonly IBrush WarningBrush = new ImmutableSolidColorBrush(Color.Parse(InstallerThemeColors.WarningHex));
    private static readonly IBrush FailBrush = new ImmutableSolidColorBrush(Color.Parse(InstallerThemeColors.DangerHex));

    private DoctorStatusToBrushConverter()
    {
    }

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value switch
        {
            DoctorCheckStatus.Pass => PassBrush,
            DoctorCheckStatus.Warning => WarningBrush,
            DoctorCheckStatus.Fail => FailBrush,
            _ => WarningBrush
        };

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class ProvisioningStatusToBrushConverter : IValueConverter
{
    public static readonly ProvisioningStatusToBrushConverter Instance = new();

    private static readonly IBrush CompletedBrush = new ImmutableSolidColorBrush(Color.Parse(InstallerThemeColors.SuccessHex));
    private static readonly IBrush WarningBrush = new ImmutableSolidColorBrush(Color.Parse(InstallerThemeColors.WarningHex));
    private static readonly IBrush FailedBrush = new ImmutableSolidColorBrush(Color.Parse(InstallerThemeColors.DangerHex));
    private static readonly IBrush ActiveBrush = new ImmutableSolidColorBrush(Color.Parse(InstallerThemeColors.PrimaryHex));
    private static readonly IBrush MutedBrush = new ImmutableSolidColorBrush(Color.Parse(InstallerThemeColors.MutedHex));

    private ProvisioningStatusToBrushConverter()
    {
    }

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value switch
        {
            ProvisioningStatus.Completed => CompletedBrush,
            ProvisioningStatus.RebootRequired => WarningBrush,
            ProvisioningStatus.Failed => FailedBrush,
            ProvisioningStatus.InProgress => ActiveBrush,
            _ => MutedBrush
        };

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class BoolToStatusBrushConverter : IValueConverter
{
    public static readonly BoolToStatusBrushConverter Instance = new();

    private static readonly IBrush TrueBrush = new ImmutableSolidColorBrush(Color.Parse(InstallerThemeColors.SuccessHex));
    private static readonly IBrush FalseBrush = new ImmutableSolidColorBrush(Color.Parse(InstallerThemeColors.MutedHex));

    private BoolToStatusBrushConverter()
    {
    }

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? TrueBrush : FalseBrush;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
