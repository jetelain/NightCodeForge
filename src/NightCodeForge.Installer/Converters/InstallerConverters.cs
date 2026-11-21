namespace NightCodeForge.Installer.Converters;

using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using NightCodeForge.Installer.Core.Models;

internal static class ConverterParameterParser
{
    internal static bool TryParseInt(object? parameter, CultureInfo culture, out int value)
    {
        switch (parameter)
        {
            case int intParameter:
                value = intParameter;
                return true;
            case string stringParameter:
                return int.TryParse(stringParameter, NumberStyles.Integer, culture, out value);
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
        ConverterParameterParser.TryParseInt(parameter, culture, out var parameterInt) &&
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
        ConverterParameterParser.TryParseInt(parameter, culture, out var parameterInt) &&
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

    private static readonly IBrush PassBrush = new SolidColorBrush(Color.Parse("#10B981"));
    private static readonly IBrush WarningBrush = new SolidColorBrush(Color.Parse("#F59E0B"));
    private static readonly IBrush FailBrush = new SolidColorBrush(Color.Parse("#EF4444"));

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

    private static readonly IBrush CompletedBrush = new SolidColorBrush(Color.Parse("#10B981"));
    private static readonly IBrush WarningBrush = new SolidColorBrush(Color.Parse("#F59E0B"));
    private static readonly IBrush FailedBrush = new SolidColorBrush(Color.Parse("#EF4444"));
    private static readonly IBrush ActiveBrush = new SolidColorBrush(Color.Parse("#6366F1"));
    private static readonly IBrush MutedBrush = new SolidColorBrush(Color.Parse("#94A3B8"));

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

    private static readonly IBrush TrueBrush = new SolidColorBrush(Color.Parse("#10B981"));
    private static readonly IBrush FalseBrush = new SolidColorBrush(Color.Parse("#94A3B8"));

    private BoolToStatusBrushConverter()
    {
    }

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? TrueBrush : FalseBrush;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
