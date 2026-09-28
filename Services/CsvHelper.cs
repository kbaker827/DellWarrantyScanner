using System.Text;

namespace DellWarrantyScanner.Services;

public static class CsvHelper
{
    private static readonly string[] TagHeaderKeywords = { "tag", "serial", "asset" };

    // Picks the delimiter used in the first line: comma, semicolon (European Excel) or tab.
    public static char DetectDelimiter(string firstLine)
    {
        char[] candidates = { ',', ';', '\t' };
        return candidates
            .OrderByDescending(c => CountOutsideQuotes(firstLine, c))
            .First();
    }

    // Parses one CSV line per RFC 4180 (quoted fields, doubled quotes).
    // Quoted fields spanning multiple lines are not supported.
    public static string[] ParseLine(string line, char delimiter = ',')
    {
        var fields = new List<string>();
        var field = new StringBuilder();
        bool inQuotes = false;

        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (inQuotes)
            {
                if (c == '"' && i + 1 < line.Length && line[i + 1] == '"') { field.Append('"'); i++; }
                else if (c == '"') inQuotes = false;
                else field.Append(c);
            }
            else if (c == '"') inQuotes = true;
            else if (c == delimiter) { fields.Add(field.ToString()); field.Clear(); }
            else field.Append(c);
        }
        fields.Add(field.ToString());
        return fields.ToArray();
    }

    public static bool LooksLikeTagHeader(string header) =>
        TagHeaderKeywords.Any(k => header.Contains(k, StringComparison.OrdinalIgnoreCase));

    // Quotes a field when needed. Text fields that start with a formula character
    // are prefixed with an apostrophe so Excel won't evaluate them (CSV injection),
    // since hostnames and API messages come from the network.
    public static string Escape(string value, bool isText = true)
    {
        if (isText && value.Length > 0 && value[0] is '=' or '+' or '-' or '@' or '\t' or '\r')
            value = "'" + value;

        return value.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0
            ? $"\"{value.Replace("\"", "\"\"")}\""
            : value;
    }

    private static int CountOutsideQuotes(string line, char c)
    {
        int count = 0;
        bool inQuotes = false;
        foreach (char ch in line)
        {
            if (ch == '"') inQuotes = !inQuotes;
            else if (ch == c && !inQuotes) count++;
        }
        return count;
    }
}
