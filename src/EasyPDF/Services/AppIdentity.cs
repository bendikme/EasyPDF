namespace EasyPDF.Services;

/// <summary>What the app is called and who publishes it.</summary>
public static class AppIdentity
{
    public const string Name = "EasyPDF";

    public const string Publisher = "DYNATEC";

    public static string Version { get; } =
        typeof(AppIdentity).Assembly.GetName().Version is { } v ? $"{v.Major}.{v.Minor}.{v.Build}" : "1.0.0";
}
