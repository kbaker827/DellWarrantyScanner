namespace DellWarrantyScanner.Models;

public static class ServiceTags
{
    // Dell service tags are 7 alphanumeric characters (5 on older systems).
    public const int MinLength = 5;
    public const int MaxLength = 7;

    public const string FormatDescription = "5–7 letters and digits";

    public static string Normalize(string value) => value.Trim().ToUpperInvariant();

    public static bool IsValid(string normalized) =>
        normalized.Length is >= MinLength and <= MaxLength &&
        normalized.All(char.IsAsciiLetterOrDigit);

    // Splits free-form input (one per line, or comma/semicolon/space separated)
    // into distinct, valid, upper-cased tags.
    public static List<string> Parse(string input) =>
        FilterValid(input.Split(new[] { '\n', '\r', ',', ';', ' ', '\t' },
            StringSplitOptions.RemoveEmptyEntries));

    public static List<string> FilterValid(IEnumerable<string> values) =>
        values
            .Select(Normalize)
            .Where(IsValid)
            .Distinct()
            .ToList();
}
