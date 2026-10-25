using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows.Data;
using System.Windows.Markup;

namespace AppEgress.Localization;

/// <summary>
/// Строки интерфейса из встроенных JSON-файлов (Localization/*.json) с переключением языка на лету.
/// XAML берёт строки через {l:Tr Ключ}: это привязка к индексатору, которая обновляется при смене языка.
/// Код использует Loc.T / Loc.Plural и перечитывает строки по событию Changed.
/// </summary>
public sealed class Loc : INotifyPropertyChanged
{
    public const string Auto = "auto";
    public const string Fallback = "en";

    /// <summary>Доступные языки: код и самоназвание. Новый язык = новый JSON + строка здесь.</summary>
    public static IReadOnlyList<(string Code, string NativeName)> Languages { get; } =
    [
        ("en", "English"),
        ("ru", "Русский"),
        ("zh-CN", "简体中文"),
    ];

    public static Loc Instance { get; } = new();

    private static readonly Dictionary<string, Dictionary<string, JsonElement>> Tables = new();
    private static IReadOnlyDictionary<string, JsonElement> _current = new Dictionary<string, JsonElement>();
    private static IReadOnlyDictionary<string, JsonElement> _fallback = new Dictionary<string, JsonElement>();

    /// <summary>Текущий язык интерфейса (код из <see cref="Languages"/>).</summary>
    public static string Language { get; private set; } = Fallback;

    /// <summary>Культура для чисел и дат в текущем языке.</summary>
    public static CultureInfo Culture { get; private set; } = CultureInfo.GetCultureInfo(Fallback);

    /// <summary>Язык сменился. Подписчики перечитывают строки, сформированные в коде.</summary>
    public static event Action? Changed;

    public event PropertyChangedEventHandler? PropertyChanged;

    private Loc() { }

    /// <summary>Строка для XAML-привязок.</summary>
    public string this[string key] => T(key);

    /// <summary>
    /// Selects the configured language, or detects a supported Windows language for "auto".
    /// Returns the selected language code.
    /// </summary>
    public static string SetLanguage(string? preference)
    {
        string code = Resolve(preference);
        _fallback = Table(Fallback);
        _current = Table(code);
        Language = code;
        Culture = CultureInfo.GetCultureInfo(code);
        Instance.PropertyChanged?.Invoke(Instance, new PropertyChangedEventArgs(Binding.IndexerName));
        Changed?.Invoke();
        return code;
    }

    public static string Resolve(string? preference)
    {
        if (!string.IsNullOrEmpty(preference) && preference != Auto && Languages.Any(l => l.Code == preference))
            return preference;
        return DetectSystem();
    }

    /// <summary>Язык для «Авто» на этой машине.</summary>
    public static string DetectSystem() => SystemLanguageOverride ?? Detect(CultureInfo.CurrentUICulture, FirstPreferredLanguage());

    /// <summary>Подмена системного языка для тестов и снимков экрана.</summary>
    internal static string? SystemLanguageOverride { get; set; }

    /// <summary>
    /// Prefer a supported Windows UI language, then the user's first preferred language.
    /// Traditional Chinese is not automatically treated as Simplified Chinese.
    /// </summary>
    public static string Detect(CultureInfo ui, string? firstPreferred = null)
    {
        return SupportedLanguage(ui.Name) ?? SupportedLanguage(firstPreferred) ?? Fallback;
    }

    private static string? SupportedLanguage(string? tag)
    {
        if (string.IsNullOrEmpty(tag))
            return null;
        if (tag.Equals("ru", StringComparison.OrdinalIgnoreCase) || tag.StartsWith("ru-", StringComparison.OrdinalIgnoreCase))
            return "ru";
        if (tag.Equals("zh", StringComparison.OrdinalIgnoreCase)
            || tag.Equals("zh-CN", StringComparison.OrdinalIgnoreCase)
            || tag.Equals("zh-SG", StringComparison.OrdinalIgnoreCase)
            || tag.Equals("zh-Hans", StringComparison.OrdinalIgnoreCase)
            || tag.StartsWith("zh-Hans-", StringComparison.OrdinalIgnoreCase))
            return "zh-CN";
        return null;
    }

    /// <summary>Первый язык из списка пользователя (HKCU\Control Panel\International\User Profile\Languages).</summary>
    private static string? FirstPreferredLanguage()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Control Panel\International\User Profile");
            return (key?.GetValue("Languages") as string[])?.FirstOrDefault();
        }
        catch (Exception)
        {
            return null;
        }
    }

    public static string T(string key)
    {
        if (_current.TryGetValue(key, out var v) && v.ValueKind == JsonValueKind.String)
            return v.GetString()!;
        if (_fallback.TryGetValue(key, out v) && v.ValueKind == JsonValueKind.String)
            return v.GetString()!;
        return key;
    }

    public static string T(string key, params object?[] args) => string.Format(Culture, T(key), args);

    /// <summary>
    /// Строка с числом и правильной формой множественного числа. В JSON значение — объект с формами:
    /// английский one/other, русский one/few/many, китайский other.
    /// </summary>
    public static string Plural(string key, long n)
    {
        string form = PluralForm(Language, n);
        if (TryForm(_current, key, form, out string? s) || TryForm(_fallback, key, PluralForm(Fallback, n), out s))
            return string.Format(Culture, s!, n);
        return $"{n} {key}";
    }

    internal static string PluralForm(string language, long n)
    {
        if (language == "zh-CN")
            return "other";
        if (language == "ru")
        {
            long n100 = Math.Abs(n) % 100, n10 = n100 % 10;
            if (n100 is >= 11 and <= 14) return "many";
            if (n10 == 1) return "one";
            if (n10 is >= 2 and <= 4) return "few";
            return "many";
        }
        return Math.Abs(n) == 1 ? "one" : "other";
    }

    private static bool TryForm(IReadOnlyDictionary<string, JsonElement> table, string key, string form, out string? value)
    {
        value = null;
        if (!table.TryGetValue(key, out var v) || v.ValueKind != JsonValueKind.Object)
            return false;
        if (v.TryGetProperty(form, out var f) || v.TryGetProperty("other", out f) || v.TryGetProperty("many", out f))
            value = f.GetString();
        return value != null;
    }

    /// <summary>Все ключи языка (для тестов полноты перевода).</summary>
    internal static IReadOnlyDictionary<string, JsonElement> Table(string code)
    {
        lock (Tables)
        {
            if (Tables.TryGetValue(code, out var t))
                return t;
            t = Load(code);
            Tables[code] = t;
            return t;
        }
    }

    private static Dictionary<string, JsonElement> Load(string code)
    {
        using var stream = typeof(Loc).Assembly.GetManifestResourceStream($"lang.{code}.json");
        if (stream == null)
            return new Dictionary<string, JsonElement>();
        using var doc = JsonDocument.Parse(stream, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip });
        return doc.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());
    }

    static Loc()
    {
        _fallback = Table(Fallback);
        _current = _fallback;
    }
}

/// <summary>{l:Tr Ключ} — локализованная строка в XAML, обновляется при смене языка.</summary>
[MarkupExtensionReturnType(typeof(string))]
public sealed class TrExtension : MarkupExtension
{
    public TrExtension() { }

    public TrExtension(string key)
    {
        Key = key;
    }

    [ConstructorArgument("key")]
    public string Key { get; set; } = "";

    public override object ProvideValue(IServiceProvider serviceProvider) =>
        new Binding($"[{Key}]") { Source = Loc.Instance, Mode = BindingMode.OneWay }.ProvideValue(serviceProvider);
}
