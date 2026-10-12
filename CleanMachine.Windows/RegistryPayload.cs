using System.Text;
using System.Text.RegularExpressions;

namespace CleanMachine.Windows;

/// <summary>Only accepts reg-export style assignments inside the recorded HKCU
/// scope. No delete directives, hive aliases, embedded sections or unrecognized
/// syntax are handed to reg.exe. Bounds apply before allocating lines.</summary>
internal static class RegistryPayload
{
    internal const int MaxBytes = 16 * 1024 * 1024;
    internal const int MaxLineChars = 256 * 1024;
    private static readonly Regex Assignment = new(
        "^(?:@|\"(?:[^\"\\\\]|\\\\[\"\\\\])*\")=(?:\"(?:[^\"\\\\]|\\\\[\"\\\\])*\"|dword:[0-9a-fA-F]{8}|hex(?:\\([0-9a-fA-F]{1,8}\\))?:.*)$",
        RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    internal static async Task ValidateAsync(Stream stream, string scope, CancellationToken token = default)
    {
        if (!RegistryCareService.IsRestorableScope(scope) || stream.Length > MaxBytes)
            throw new InvalidDataException("Restore scope or payload size is invalid.");
        stream.Position = 0;
        using var reader = new StreamReader(stream, Encoding.UTF8, true, 4096, leaveOpen: true);
        var text = await reader.ReadToEndAsync(token); // length was bounded on the held file
        if (text.Contains('\0')) throw new InvalidDataException("Registry payload contains NUL characters.");
        var lines = text.Split('\n');
        var header = lines[0].Trim('\uFEFF', '\r', ' ', '\t');
        if (header is not ("Windows Registry Editor Version 5.00" or "REGEDIT4"))
            throw new InvalidDataException("Invalid registry export header.");
        var inSection = false;
        var sections = 0;
        var continuation = false;
        var prefix = "HKEY_CURRENT_USER\\" + scope.TrimEnd('\\');
        foreach (var raw in lines.Skip(1))
        {
            token.ThrowIfCancellationRequested();
            if (raw.Length > MaxLineChars) throw new InvalidDataException("Registry export line exceeds the limit.");
            var line = raw.Trim();
            if (continuation)
            {
                continuation = ValidateHex(line);
                continue;
            }
            if (line.Length == 0 || line.StartsWith(';')) continue;
            if (line.StartsWith('['))
            {
                if (!line.EndsWith(']')) throw new InvalidDataException("Malformed registry section.");
                var key = line[1..^1];
                if (key.Contains('[') || key.Contains(']') || key.Contains("..", StringComparison.Ordinal)
                    || !(key.Equals(prefix, StringComparison.OrdinalIgnoreCase)
                         || key.StartsWith(prefix + "\\", StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidDataException("Registry section lies outside the recorded HKCU scope.");
                inSection = true;
                if (++sections > 10000) throw new InvalidDataException("Registry export contains too many sections.");
                continue;
            }
            if (!inSection || !Assignment.IsMatch(line))
                throw new InvalidDataException("Unrecognized or destructive registry directive.");
            // The value name can contain '='; locate the assignment after its quoted name.
            var at = line[0] == '@' ? 2 : EndName(line) + 2;
            var value = line[at..];
            if (value.StartsWith("hex", StringComparison.Ordinal))
                continuation = ValidateHex(value[(value.IndexOf(':') + 1)..]);
        }
        if (continuation || sections == 0) throw new InvalidDataException("Incomplete registry export.");
        stream.Position = 0;
    }

    private static int EndName(string line)
    {
        for (var i = 1; i < line.Length; i++)
        {
            if (line[i] == '\\') { i++; continue; }
            if (line[i] == '"') return i;
        }
        throw new InvalidDataException("Malformed value name.");
    }

    private static bool ValidateHex(string value)
    {
        var more = value.EndsWith('\\');
        if (more) value = value[..^1].TrimEnd();
        if (more && !value.EndsWith(',')) throw new InvalidDataException("Malformed hex continuation.");
        foreach (var hex in value.Split(','))
        {
            var part = hex.Trim();
            if (part.Length == 0) continue;
            if (part.Length != 2 || !part.All(char.IsAsciiHexDigit))
                throw new InvalidDataException("Malformed hex registry value.");
        }
        return more;
    }
}
