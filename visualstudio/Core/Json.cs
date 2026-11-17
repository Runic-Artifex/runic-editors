using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace RunicArtifex.DevKit.VisualStudio.Core;

/// <summary>
/// A small strict JSON reader for the documents of <c>dotnet runic</c> (the doctor envelope, the development source map,
/// tool manifests). Core has no package dependencies (package.py ships only its assembly), so it cannot use
/// System.Text.Json on netstandard2.0. Objects read as <see cref="Dictionary{TKey,TValue}"/>, arrays as
/// <see cref="List{T}"/>, numbers as <see cref="double"/>.
/// </summary>
internal static class Json
{
    public static object? Parse(string text)
    {
        var reader = new Reader(text);
        reader.SkipWhitespace();
        object? value = reader.ReadValue(0);
        reader.SkipWhitespace();
        if (!reader.AtEnd) throw reader.Error("Unexpected text after the JSON value");
        return value;
    }

    public static Dictionary<string, object?>? Object(object? value) => value as Dictionary<string, object?>;
    public static List<object?>? Array(object? value) => value as List<object?>;

    public static string? String(Dictionary<string, object?>? source, string key)
        => source != null && source.TryGetValue(key, out var value) ? value as string : null;

    public static double? Number(Dictionary<string, object?>? source, string key)
        => source != null && source.TryGetValue(key, out var value) && value is double number ? number : null;

    public static bool? Boolean(Dictionary<string, object?>? source, string key)
        => source != null && source.TryGetValue(key, out var value) && value is bool flag ? flag : null;

    public static object? Get(Dictionary<string, object?>? source, string key)
        => source != null && source.TryGetValue(key, out var value) ? value : null;

    private sealed class Reader
    {
        private const int MaxDepth = 128;
        private readonly string text;
        private int position;

        public Reader(string text) { this.text = text; }

        public bool AtEnd => position >= text.Length;

        public FormatException Error(string message) => new($"{message} at offset {position}.");

        public void SkipWhitespace()
        {
            while (position < text.Length && text[position] is ' ' or '\t' or '\r' or '\n' or '﻿') position++;
        }

        public object? ReadValue(int depth)
        {
            if (depth > MaxDepth) throw Error("JSON nests too deeply");
            if (AtEnd) throw Error("Unexpected end of JSON");
            char next = text[position];
            switch (next)
            {
                case '{': return ReadObject(depth);
                case '[': return ReadArray(depth);
                case '"': return ReadString();
                case 't': Expect("true"); return true;
                case 'f': Expect("false"); return false;
                case 'n': Expect("null"); return null;
                default:
                    if (next == '-' || (next >= '0' && next <= '9')) return ReadNumber();
                    throw Error($"Unexpected character '{next}'");
            }
        }

        private void Expect(string word)
        {
            if (string.CompareOrdinal(text, position, word, 0, word.Length) != 0) throw Error($"Expected {word}");
            position += word.Length;
        }

        private Dictionary<string, object?> ReadObject(int depth)
        {
            var result = new Dictionary<string, object?>(StringComparer.Ordinal);
            position++;
            SkipWhitespace();
            if (Peek('}')) { position++; return result; }
            for (;;)
            {
                SkipWhitespace();
                if (!Peek('"')) throw Error("Expected a property name");
                string key = ReadString();
                SkipWhitespace();
                if (!Peek(':')) throw Error("Expected ':'");
                position++;
                SkipWhitespace();
                result[key] = ReadValue(depth + 1);
                SkipWhitespace();
                if (Peek(',')) { position++; continue; }
                if (Peek('}')) { position++; return result; }
                throw Error("Expected ',' or '}'");
            }
        }

        private List<object?> ReadArray(int depth)
        {
            var result = new List<object?>();
            position++;
            SkipWhitespace();
            if (Peek(']')) { position++; return result; }
            for (;;)
            {
                SkipWhitespace();
                result.Add(ReadValue(depth + 1));
                SkipWhitespace();
                if (Peek(',')) { position++; continue; }
                if (Peek(']')) { position++; return result; }
                throw Error("Expected ',' or ']'");
            }
        }

        private bool Peek(char character) => position < text.Length && text[position] == character;

        private string ReadString()
        {
            position++;
            var builder = new StringBuilder();
            while (position < text.Length)
            {
                char character = text[position++];
                if (character == '"') return builder.ToString();
                if (character < ' ') throw Error("Control character in a string");
                if (character != '\\') { builder.Append(character); continue; }
                if (AtEnd) break;
                char escape = text[position++];
                switch (escape)
                {
                    case '"': builder.Append('"'); break;
                    case '\\': builder.Append('\\'); break;
                    case '/': builder.Append('/'); break;
                    case 'b': builder.Append('\b'); break;
                    case 'f': builder.Append('\f'); break;
                    case 'n': builder.Append('\n'); break;
                    case 'r': builder.Append('\r'); break;
                    case 't': builder.Append('\t'); break;
                    case 'u':
                        if (position + 4 > text.Length || !int.TryParse(text.Substring(position, 4), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out int code))
                            throw Error("Invalid \\u escape");
                        builder.Append((char)code);
                        position += 4;
                        break;
                    default: throw Error($"Invalid escape '\\{escape}'");
                }
            }
            throw Error("Unterminated string");
        }

        private double ReadNumber()
        {
            int start = position;
            if (Peek('-')) position++;
            while (position < text.Length && (char.IsDigit(text[position]) || text[position] is '.' or 'e' or 'E' or '+' or '-')) position++;
            if (!double.TryParse(text.Substring(start, position - start), NumberStyles.Float, CultureInfo.InvariantCulture, out double value))
                throw Error("Invalid number");
            return value;
        }
    }
}
