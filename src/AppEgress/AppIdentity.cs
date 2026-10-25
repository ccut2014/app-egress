namespace AppEgress;

internal static class AppIdentity
{
    public const string Name = "AppEgress";
    public const string LegacyName = "TrafikRuler";

    // Share the existing instance guard: two differently branded packet engines must not run together.
    public const string SingleInstanceMutex = @"Global\TrafikRuler.SingleInstance";
    public const string ShowEvent = @"Global\TrafikRuler.Show";

    // Reuse the old scheduler slot. Enabling autostart explicitly updates its executable path.
    public const string AutostartTask = "TrafikRuler";
}
