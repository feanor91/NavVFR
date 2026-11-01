using System.Text;

namespace VFRNav.Core.Services;

/// <summary>Lecteur CSV minimal (guillemets, virgules et sauts de ligne dans les champs).</summary>
public static class CsvParser
{
    public static IEnumerable<string[]> Parse(TextReader reader, char delimiter = ',')
    {
        var row = new List<string>();
        var field = new StringBuilder();
        bool quoted = false;
        int ch;
        while ((ch = reader.Read()) != -1)
        {
            char c = (char)ch;
            if (quoted)
            {
                if (c == '"')
                {
                    if (reader.Peek() == '"') { reader.Read(); field.Append('"'); }
                    else quoted = false;
                }
                else field.Append(c);
            }
            else if (c == '"') quoted = true;
            else if (c == delimiter) { row.Add(field.ToString()); field.Clear(); }
            else if (c == '\n') { row.Add(field.ToString()); field.Clear(); yield return row.ToArray(); row.Clear(); }
            else if (c != '\r') field.Append(c);
        }
        if (field.Length > 0 || row.Count > 0) { row.Add(field.ToString()); yield return row.ToArray(); }
    }
}
