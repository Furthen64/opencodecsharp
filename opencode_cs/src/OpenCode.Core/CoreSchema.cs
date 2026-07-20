using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace OpenCode.Core;

public static class CoreSchema
{
    public static string CreateAbsolutePath(string path) => path;
    public static string CreateRelativePath(string path) => path;

    public static bool IsPositiveInt(int value) => value > 0;
    public static bool IsNonNegativeInt(int value) => value >= 0;
}

public static class ModelRef
{
    public static (string providerID, string modelID) Parse(string input)
    {
        var parts = input.Split('/', 2);
        return (parts[0], parts.Length > 1 ? parts[1] : string.Empty);
    }
}
