namespace AutoScript.Services;

/// <summary>Reflow long paragraphs without adding, deleting or rewriting words.</summary>
public static class ScriptFormatter
{
    public static string Reflow(string text)
    {
        var result = new List<string>();
        foreach (var line in text.Replace("\r\n", "\n").Split('\n')) {
            var words = System.Text.RegularExpressions.Regex.Matches(line, @"\S+").Select(x => x.Value).ToArray();
            if (words.Length <= 110) { result.Add(line); continue; }
            int offset = 0;
            while (words.Length - offset > 60) {
                int count = 60;
                for (int i = 59; i >= 19; i--) {
                    var word = words[offset + i].TrimEnd('"', '\'');
                    if (word.EndsWith('.') || word.EndsWith('?') || word.EndsWith('!')) { count = i + 1; break; }
                }
                result.Add(string.Join(" ", words.Skip(offset).Take(count))); offset += count;
            }
            if (offset < words.Length) result.Add(string.Join(" ", words.Skip(offset)));
        }
        return string.Join("\n", result);
    }
}
