using System;

namespace BambuVideoStream.Utilities;

internal static class StringExtensions
{
    /// <summary>
    /// Ensures a string ends with a particular suffix
    /// </summary>
    public static string EnsureSuffix(this string fileName, string extension)
    {
        if (string.IsNullOrEmpty(fileName) || fileName.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
        {
            return fileName;
        }

        return fileName + extension;
    }
}
