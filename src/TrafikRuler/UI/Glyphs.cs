using TrafikRuler.Core;

namespace TrafikRuler.UI;

/// <summary>Коды значков шрифта Segoe Fluent Icons (на Windows 10 те же коды в Segoe MDL2 Assets).</summary>
internal static class Glyphs
{
    public const string Ethernet = "";
    public const string WiFi = "";
    public const string Vpn = "";
    public const string Globe = "";
    public const string Add = "";
    public const string Pause = "";
    public const string Play = "";
    public const string Settings = "";
    public const string More = "";
    public const string Edit = "";
    public const string Delete = "";
    public const string Refresh = "";
    public const string Search = "";
    public const string Folder = "";
    public const string Forward = "";
    public const string History = "";
    public const string Down = "";
    public const string Up = "";
    public const string App = "";
    public const string Error = "";
    public const string Shield = "";

    public static string ForAdapter(AdapterKind kind) => kind switch
    {
        AdapterKind.Ethernet => Ethernet,
        AdapterKind.WiFi => WiFi,
        AdapterKind.Vpn => Vpn,
        _ => Globe,
    };
}
