using System.Globalization;
using System.Windows;
using System.Windows.Data;
using EasyPDF.Resources;
using EasyPDF.Services;

namespace EasyPDF.Views;

internal static class ConverterUtil
{
    internal static bool IsInverted(object? parameter) =>
        parameter is string s && (s.Equals("invert", StringComparison.OrdinalIgnoreCase) || s == "!");

    internal static Visibility ToVisibility(bool visible, object? parameter) =>
        visible ^ IsInverted(parameter) ? Visibility.Visible : Visibility.Collapsed;
}

public sealed class BoolToVisibilityConverter : IValueConverter
{
    public static readonly BoolToVisibilityConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        ConverterUtil.ToVisibility(value is true, parameter);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is Visibility v && (v == Visibility.Visible) ^ ConverterUtil.IsInverted(parameter);
}

public sealed class StringToVisibilityConverter : IValueConverter
{
    public static readonly StringToVisibilityConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        ConverterUtil.ToVisibility(value is string s && s.Length > 0, parameter);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

public sealed class CountToVisibilityConverter : IValueConverter
{
    public static readonly CountToVisibilityConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        ConverterUtil.ToVisibility(value is int n && n > 0, parameter);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

public sealed class NullToVisibilityConverter : IValueConverter
{
    public static readonly NullToVisibilityConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        ConverterUtil.ToVisibility(value is not null, parameter);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

/// <summary>"Just now", "5 minutes ago", "Yesterday", or the date, in the UI language.</summary>
public sealed class RelativeTimeConverter : IValueConverter
{
    public static readonly RelativeTimeConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is DateTime utc ? Describe(utc, DateTime.UtcNow) : string.Empty;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;

    public static string Describe(DateTime utc, DateTime nowUtc)
    {
        var age = nowUtc - utc;
        if (age < TimeSpan.FromMinutes(1)) return Strings.Time_JustNow;
        if (age < TimeSpan.FromHours(1)) return UiLanguage.Plural((long)age.TotalMinutes, Strings.Time_MinuteAgo, Strings.Time_MinutesAgo);

        var local = utc.ToLocalTime();
        var today = nowUtc.ToLocalTime().Date;
        if (local.Date == today) return UiLanguage.Format(Strings.Time_TodayAt, local.ToString("t", UiLanguage.Culture));
        if (local.Date == today.AddDays(-1)) return UiLanguage.Format(Strings.Time_YesterdayAt, local.ToString("t", UiLanguage.Culture));
        return local.ToString(local.Year == today.Year ? Strings.Time_DateFormat : Strings.Time_DateYearFormat, UiLanguage.Culture);
    }
}
