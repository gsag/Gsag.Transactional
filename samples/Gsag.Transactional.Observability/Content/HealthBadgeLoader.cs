using System.Reflection;
using Microsoft.AspNetCore.Http;

namespace Gsag.Transactional.Observability.Content;

internal static class HealthBadgeLoader
{
    private static readonly string ResourceName = $"{typeof(HealthBadgeLoader).Namespace}.health-badge.html";

    private static readonly string Template =
        Assembly.GetExecutingAssembly()
            .GetManifestResourceStream(ResourceName) is { } stream
            ? ReadStream(stream)
            : throw new InvalidOperationException($"Embedded resource '{ResourceName}' not found.");

    internal static string Render(string cssClass, PathString path, string label) =>
        Template
            .Replace("__CSS_CLASS__", cssClass)
            .Replace("__PATH__", path.Value ?? string.Empty)
            .Replace("__LABEL__", label);

    private static string ReadStream(Stream stream)
    {
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
