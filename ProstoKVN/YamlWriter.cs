using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace ProstoKVN;

public static class YamlWriter
{
    public static string Serialize(object? data)
    {
        var output = new StringBuilder("# ProstoKVN generated configuration\n");
        Write(output, data, 0);
        return output.ToString();
    }
    private static string Scalar(object? value)
    {
        if (value == null) return "null";
        if (value is bool b) return b ? "true" : "false";
        if (value is string s) return "'" + s.Replace("'", "''").Replace("\r", "").Replace("\n", " ") + "'";
        if (value is IFormattable format) return format.ToString(null, CultureInfo.InvariantCulture);
        return "'" + value.ToString()?.Replace("'", "''") + "'";
    }
    private static void Write(StringBuilder sb, object? value, int level)
    {
        string indent = new(' ', level);
        if (value is IDictionary map)
        {
            foreach (DictionaryEntry item in map)
            {
                string key = Convert.ToString(item.Key, CultureInfo.InvariantCulture) ?? "";
                sb.Append(indent).Append(key).Append(':');
                if (IsNested(item.Value))
                {
                    sb.AppendLine();
                    Write(sb, item.Value, level + 2);
                }
                else sb.Append(' ').AppendLine(Scalar(item.Value));
            }
        }
        else if (value is IEnumerable items && value is not string)
        {
            foreach (var item in items)
            {
                sb.Append(indent).Append('-');
                if (IsNested(item))
                {
                    sb.AppendLine();
                    Write(sb, item, level + 2);
                }
                else sb.Append(' ').AppendLine(Scalar(item));
            }
        }
        else sb.Append(indent).AppendLine(Scalar(value));
    }
    private static bool IsNested(object? item) => item is IDictionary || item is IEnumerable && item is not string;
}