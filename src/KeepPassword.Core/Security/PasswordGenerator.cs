using System.Security.Cryptography;

namespace KeepPassword.Core.Security;

public enum PasswordGeneratorKind
{
    Random,
    Memorable,
    Pin
}

public sealed class PasswordGeneratorOptions
{
    public static PasswordGeneratorOptions DefaultLogin { get; } = new()
    {
        Kind = PasswordGeneratorKind.Random,
        Length = 16,
        Lowercase = true,
        Uppercase = true,
        Digits = true,
        Symbols = false,
        ExcludeAmbiguous = true
    };

    public PasswordGeneratorKind Kind { get; init; } = PasswordGeneratorKind.Random;

    public int Length { get; init; } = 16;

    public bool Lowercase { get; init; } = true;

    public bool Uppercase { get; init; } = true;

    public bool Digits { get; init; } = true;

    public bool Symbols { get; init; }

    public bool ExcludeAmbiguous { get; init; } = true;

    public bool Capitalize { get; init; }

    public bool FullWords { get; init; } = true;

    public void Validate()
    {
        if (Length is < 8 or > 128)
        {
            throw new ArgumentOutOfRangeException(nameof(Length), "密码长度需在 8 到 128 之间。");
        }

        if (!Lowercase && !Uppercase && !Digits && !Symbols)
        {
            throw new ArgumentException("至少选择一类字符。");
        }
    }
}

public enum PasswordStrength
{
    Empty,
    Weak,
    Fair,
    Strong
}

public static class PasswordStrengthEvaluator
{
    private static readonly string[] Common =
    [
        "password", "passw0rd", "123456", "12345678", "123456789", "qwerty", "abc123", "letmein", "admin", "welcome"
    ];

    public static PasswordStrength Evaluate(string? password)
    {
        if (string.IsNullOrEmpty(password))
        {
            return PasswordStrength.Empty;
        }

        var lowered = password.ToLowerInvariant();
        if (password.Length < 10 || Common.Any(lowered.Contains))
        {
            return PasswordStrength.Weak;
        }

        var classes = CountClasses(password);
        if (password.Length >= 14 && classes >= 3)
        {
            return PasswordStrength.Strong;
        }

        if (password.Length >= 16 && classes >= 2)
        {
            return PasswordStrength.Strong;
        }

        return classes >= 2 ? PasswordStrength.Fair : PasswordStrength.Weak;
    }

    public static string Describe(PasswordStrength strength) => strength switch
    {
        PasswordStrength.Empty => "空",
        PasswordStrength.Weak => "弱",
        PasswordStrength.Fair => "中等",
        PasswordStrength.Strong => "强",
        _ => "未知"
    };

    private static int CountClasses(string password)
    {
        var classes = 0;
        if (password.Any(char.IsLower)) classes++;
        if (password.Any(char.IsUpper)) classes++;
        if (password.Any(char.IsDigit)) classes++;
        if (password.Any(character => !char.IsLetterOrDigit(character))) classes++;
        return classes;
    }
}

public static class PasswordGenerator
{
    public static string Generate(PasswordGeneratorOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return options.Kind switch
        {
            PasswordGeneratorKind.Memorable => GenerateMemorable(options),
            PasswordGeneratorKind.Pin => GeneratePin(options),
            _ => GenerateRandom(options)
        };
    }

    private static string GenerateRandom(PasswordGeneratorOptions options)
    {
        options.Validate();
        var sets = CharacterSets(options);
        var pool = string.Concat(sets);
        var chars = new char[options.Length];
        for (var i = 0; i < sets.Count; i++)
        {
            chars[i] = sets[i][RandomIndex(sets[i].Length)];
        }

        for (var i = sets.Count; i < chars.Length; i++)
        {
            chars[i] = pool[RandomIndex(pool.Length)];
        }

        Shuffle(chars);
        return new string(chars);
    }

    private static string GeneratePin(PasswordGeneratorOptions options)
    {
        if (options.Length is < 4 or > 12)
        {
            throw new ArgumentOutOfRangeException(nameof(options.Length), "PIN 长度需在 4 到 12 之间。");
        }

        const string digits = "0123456789";
        var chars = new char[options.Length];
        for (var i = 0; i < chars.Length; i++)
        {
            chars[i] = digits[RandomIndex(digits.Length)];
        }

        return new string(chars);
    }

    private static string GenerateMemorable(PasswordGeneratorOptions options)
    {
        if (options.Length is < 3 or > 10)
        {
            throw new ArgumentOutOfRangeException(nameof(options.Length), "单词数量需在 3 到 10 之间。");
        }

        var used = new HashSet<int>();
        var words = new string[options.Length];
        for (var i = 0; i < words.Length; i++)
        {
            var index = NextWordIndex(used);
            used.Add(index);
            var word = MemorableWords[index];
            if (!options.FullWords && word.Length > 4)
            {
                word = word[..4];
            }

            if (options.Capitalize)
            {
                word = char.ToUpperInvariant(word[0]) + word[1..];
            }

            words[i] = word;
        }

        return string.Join('-', words);
    }

    private static int NextWordIndex(HashSet<int> used)
    {
        if (used.Count >= MemorableWords.Length)
        {
            return RandomIndex(MemorableWords.Length);
        }

        int index;
        do
        {
            index = RandomIndex(MemorableWords.Length);
        }
        while (used.Contains(index));

        return index;
    }

    private static List<string> CharacterSets(PasswordGeneratorOptions options)
    {
        var sets = new List<string>();
        if (options.Lowercase)
        {
            sets.Add(options.ExcludeAmbiguous ? "abcdefghijkmnpqrstuvwxyz" : "abcdefghijklmnopqrstuvwxyz");
        }

        if (options.Uppercase)
        {
            sets.Add(options.ExcludeAmbiguous ? "ABCDEFGHJKLMNPQRSTUVWXYZ" : "ABCDEFGHIJKLMNOPQRSTUVWXYZ");
        }

        if (options.Digits)
        {
            sets.Add(options.ExcludeAmbiguous ? "23456789" : "0123456789");
        }

        if (options.Symbols)
        {
            sets.Add("!@#$%^&*-_=+?");
        }

        return sets;
    }

    private static int RandomIndex(int length)
    {
        return RandomNumberGenerator.GetInt32(length);
    }

    private static void Shuffle(char[] chars)
    {
        for (var i = chars.Length - 1; i > 0; i--)
        {
            var swap = RandomNumberGenerator.GetInt32(i + 1);
            (chars[i], chars[swap]) = (chars[swap], chars[i]);
        }
    }

    private static readonly string[] MemorableWords =
    [
        "apple", "river", "stone", "cloud", "maple", "amber", "cedar", "daisy", "eagle", "flint",
        "grape", "honey", "ivory", "jolly", "koala", "lemon", "mango", "noble", "olive", "pearl",
        "quilt", "robin", "sunny", "tiger", "umbra", "violet", "walnut", "yacht", "zebra", "anchor",
        "bridge", "canyon", "desert", "engine", "forest", "garden", "harbor", "island", "jungle", "kettle",
        "lantern", "meadow", "needle", "orchid", "pebble", "quartz", "rocket", "silver", "temple", "umbrella",
        "valley", "willow", "yellow", "basket", "candle", "dragon", "falcon", "ginger", "hammer", "insect",
        "jacket", "kitten", "ladder", "mirror", "napkin", "orange", "pencil", "rabbit", "saddle", "turtle",
        "velvet", "window", "yogurt", "zipper", "acorn", "breeze", "copper", "donut", "elm", "feather",
        "glove", "hazel", "igloo", "jasmine", "kernel", "lilac", "marble", "nectar", "oyster", "piano",
        "ribbon", "sailor", "ticket", "unicorn", "voyage", "winter", "axiom", "butter", "castle", "dolphin",
        "ember", "fossil", "gravel", "helmet", "iris", "jigsaw", "kernel", "lotus", "mosaic", "noodle",
        "oasis", "planet", "quiver", "radar", "sphinx", "throne", "ultra", "violet", "wander", "xenon"
    ];
}
