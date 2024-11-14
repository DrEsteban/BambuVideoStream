using System;
using System.Threading.Tasks;

namespace BambuVideoStream.Utilities;

internal static class Extensions
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

    public static void Forget(this Task _)
    {
        // intentionally empty
    }
}
