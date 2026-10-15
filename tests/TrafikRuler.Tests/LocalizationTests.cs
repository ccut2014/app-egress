using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using TrafikRuler.Localization;
using TrafikRuler.UI.ViewModels;

namespace TrafikRuler.Tests;

/// <summary>Тесты меняют глобальный язык, поэтому идут в одной коллекции с UI-тестами, без параллельности.</summary>
[CollectionDefinition("Localization", DisableParallelization = true)]
public class LocalizationCollection;

[Collection("Localization")]
public class LocalizationTests
{
    private static readonly Dictionary<string, string[]> RequiredPluralForms = new()
    {
        ["en"] = ["one", "other"],
        ["ru"] = ["one", "few", "many"],
    };

    private static IReadOnlyDictionary<string, JsonElement> Table(string code) => Loc.Table(code);

    public static TheoryData<string> Translations()
    {
        var data = new TheoryData<string>();
        foreach (var (code, _) in Loc.Languages)
            if (code != Loc.Fallback)
                data.Add(code);
        return data;
    }

    [Fact]
    public void EveryLanguageHasATable()
    {
        foreach (var (code, _) in Loc.Languages)
            Assert.True(Table(code).Count > 50, $"Нет или почти пуст перевод {code}");
    }

    [Theory]
    [MemberData(nameof(Translations))]
    public void TranslationHasExactlyTheEnglishKeys(string code)
    {
        var en = Table("en").Keys.ToHashSet();
        var tr = Table(code).Keys.ToHashSet();
        Assert.Empty(en.Except(tr));   // не переведено
        Assert.Empty(tr.Except(en));   // лишнее или опечатка в ключе
    }

    [Theory]
    [MemberData(nameof(Translations))]
    public void TranslationKeepsPlaceholders(string code)
    {
        var en = Table("en");
        var tr = Table(code);
        foreach (var (key, value) in en)
        {
            if (value.ValueKind != JsonValueKind.String)
                continue;
            Assert.Equal(Placeholders(value.GetString()!), Placeholders(tr[key].GetString()!));
        }
    }

    [Fact]
    public void PluralsHaveAllForms()
    {
        foreach (var (code, _) in Loc.Languages)
        {
            foreach (var (key, value) in Table(code))
            {
                if (value.ValueKind != JsonValueKind.Object)
                    continue;
                foreach (var form in RequiredPluralForms[code])
                    Assert.True(value.TryGetProperty(form, out _), $"{code}.{key}: нет формы {form}");
            }
        }
    }

    [Fact]
    public void EveryKeyUsedInSourceExists_AndNoKeyIsUnused()
    {
        var used = UsedKeys();
        var en = Table("en").Keys.ToHashSet();
        Assert.Empty(used.Except(en));  // ключ в коде, но нет в en.json
        Assert.Empty(en.Except(used));  // ключ в en.json, но нигде не используется
    }

    [Theory]
    [InlineData("ru", 1, "one")]
    [InlineData("ru", 2, "few")]
    [InlineData("ru", 4, "few")]
    [InlineData("ru", 5, "many")]
    [InlineData("ru", 11, "many")]
    [InlineData("ru", 14, "many")]
    [InlineData("ru", 21, "one")]
    [InlineData("ru", 22, "few")]
    [InlineData("ru", 0, "many")]
    [InlineData("en", 1, "one")]
    [InlineData("en", 0, "other")]
    [InlineData("en", 2, "other")]
    public void PluralRules(string lang, long n, string form) => Assert.Equal(form, Loc.PluralForm(lang, n));

    [Theory]
    [InlineData("ru-RU", null, "ru")]
    [InlineData("en-US", null, "en")]
    [InlineData("de-DE", null, "en")]
    [InlineData("uk-UA", null, "en")]
    // Английская Windows, но русский первым в списке языков пользователя
    [InlineData("en-US", "ru", "ru")]
    [InlineData("en-US", "ru-RU", "ru")]
    [InlineData("en-US", "en-US", "en")]
    [InlineData("en-US", "uk", "en")]
    public void AutoDetection(string uiCulture, string? firstPreferred, string expected) =>
        Assert.Equal(expected, Loc.Detect(CultureInfo.GetCultureInfo(uiCulture), firstPreferred));

    [Fact]
    public void SwitchingLanguageChangesStringsAndFormats()
    {
        try
        {
            Assert.Equal("ru", Loc.SetLanguage("ru"));
            Assert.Equal("Настройки", Loc.T("Settings"));
            Assert.Equal("5 процессов", Loc.Plural("PluralProcesses", 5));
            Assert.Equal("1,5 КБ", Format.Bytes(1536));
            Assert.Equal("Настройки", Loc.Instance["Settings"]);

            Assert.Equal("en", Loc.SetLanguage("en"));
            Assert.Equal("Settings", Loc.T("Settings"));
            Assert.Equal("1 process", Loc.Plural("PluralProcesses", 1));
            Assert.Equal("1.5 KB", Format.Bytes(1536));
            Assert.Equal("2.5 MB/s", Format.Speed(2.5 * 1024 * 1024));

            // Неизвестный код — как «Авто»
            Assert.Equal(Loc.DetectSystem(), Loc.SetLanguage("xx"));
            // Отсутствующий ключ виден сразу, а не превращается в пустую строку
            Assert.Equal("NoSuchKey", Loc.T("NoSuchKey"));
        }
        finally
        {
            Loc.SetLanguage("en");
        }
    }

    [Fact]
    public void ChangedEventFires()
    {
        int calls = 0;
        void Handler() => calls++;
        Loc.Changed += Handler;
        try
        {
            Loc.SetLanguage("ru");
            Loc.SetLanguage("en");
        }
        finally
        {
            Loc.Changed -= Handler;
            Loc.SetLanguage("en");
        }
        Assert.Equal(2, calls);
    }

    private static string[] Placeholders(string s) =>
        Regex.Matches(s, @"\{(\d+)(?::[^}]*)?\}").Select(m => m.Groups[1].Value).Distinct().Order().ToArray();

    /// <summary>
    /// Ключи из исходников: Loc.T("X"…), Loc.Plural("X"…), Loc.T(условие ? "A" : "B"…) и {l:Tr X} в XAML.
    /// </summary>
    private static HashSet<string> UsedKeys()
    {
        var src = Path.Combine(RepoRoot(), "src", "TrafikRuler");
        var keys = new HashSet<string>();
        foreach (var file in Directory.EnumerateFiles(src, "*.*", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
                continue;
            if (file.EndsWith(".cs"))
            {
                string text = File.ReadAllText(file);
                foreach (Match m in Regex.Matches(text, @"Loc\.(?:T|Plural)\(\s*""(\w+)"""))
                    keys.Add(m.Groups[1].Value);
                foreach (Match m in Regex.Matches(text, @"Loc\.T\([^()""]*\?\s*""(\w+)""\s*:\s*""(\w+)"""))
                {
                    keys.Add(m.Groups[1].Value);
                    keys.Add(m.Groups[2].Value);
                }
            }
            else if (file.EndsWith(".xaml"))
            {
                foreach (Match m in Regex.Matches(File.ReadAllText(file), @"\{l:Tr\s+(\w+)\}"))
                    keys.Add(m.Groups[1].Value);
            }
        }
        return keys;
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "TrafikRuler.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Не найден корень репозитория");
    }
}
