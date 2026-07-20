using System;
using System.Security.Cryptography;

namespace OpenCode.Schema;

public static class Identifier
{
    const int Length = 26;
    const string Chars = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz";

    static long _lastTimestamp;
    static int _counter;

    public static string Ascending() => Create(false);
    public static string Descending() => Create(true);

    static string Create(bool descending)
    {
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (timestamp != _lastTimestamp)
        {
            _lastTimestamp = timestamp;
            _counter = 0;
        }
        _counter++;

        var current = (ulong)timestamp * 0x1000UL + (ulong)_counter;
        var value = descending ? ~current : current;
        var time = string.Concat(
            ((value >> 40) & 0xff).ToString("x2"),
            ((value >> 32) & 0xff).ToString("x2"),
            ((value >> 24) & 0xff).ToString("x2"),
            ((value >> 16) & 0xff).ToString("x2"),
            ((value >> 8) & 0xff).ToString("x2"),
            (value & 0xff).ToString("x2")
        );

        var bytes = new byte[Length - 12];
        RandomNumberGenerator.Fill(bytes);
        var random = new string(bytes.Select(b => Chars[b % 62]).ToArray());

        return time + random;
    }
}
