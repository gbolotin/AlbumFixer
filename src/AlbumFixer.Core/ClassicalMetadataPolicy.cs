using System.Text.RegularExpressions;

namespace AlbumFixer.Core;

internal static partial class ClassicalMetadataPolicy
{
    public static bool RequiresComposer(string? genre, string? title, bool isCompilation = false)
    {
        var value = genre ?? string.Empty;
        var classical = value.Contains("classical", StringComparison.OrdinalIgnoreCase) ||
                        value.Contains("opera", StringComparison.OrdinalIgnoreCase);
        if (!classical || NonMusicalTitle().IsMatch(title ?? string.Empty)) return false;
        var categories = Regex.Split(value, @"\s*[,;/|]\s*")
            .Where(category => !string.IsNullOrWhiteSpace(category))
            .ToArray();
        var mixedGenre = categories.Any(category =>
            !category.Contains("classical", StringComparison.OrdinalIgnoreCase) &&
            !category.Contains("opera", StringComparison.OrdinalIgnoreCase) &&
            !category.Contains("choral", StringComparison.OrdinalIgnoreCase));
        return !(mixedGenre || isCompilation) || ClassicalWorkTitle().IsMatch(title ?? string.Empty);
    }

    public static bool IsCompilationArtist(string? albumArtist) =>
        Regex.IsMatch(albumArtist?.Trim() ?? string.Empty,
            @"^various(?:\s+artists?)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    [GeneratedRegex("\\b(?:interview|conversation)\\b", RegexOptions.IgnoreCase)]
    private static partial Regex NonMusicalTitle();

    [GeneratedRegex("\\b(?:symphon(?:y|ie)|concerto|concertino|sonata|sonatine|suite|requiem|oratorio|passion|cantata|etudes?|études?|preludes?|fugue|opus|aria|pavane|motet|motectum|missa|mass|quartet|quintet|capriccio|recercada|sonnerie|vocalise|variations?|overture|fantaisie|brandenburg|in\\s+nomine|bwv|hwv)\\b|\\bop\\.?\\s*\\d+|\\bk\\.?\\s*\\d+", RegexOptions.IgnoreCase)]
    private static partial Regex ClassicalWorkTitle();
}
