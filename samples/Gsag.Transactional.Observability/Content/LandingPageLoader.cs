using System.Reflection;

namespace Gsag.Transactional.Observability.Content;

internal static class LandingPageLoader
{
    private static readonly string ResourceName = $"{typeof(LandingPageLoader).Namespace}.landing-page.html";

    internal static string Content =>
        Assembly.GetExecutingAssembly()
            .GetManifestResourceStream(ResourceName) is { } stream
            ? ReadStream(stream)
            : throw new InvalidOperationException($"Embedded resource '{ResourceName}' not found.");

    private static string ReadStream(Stream stream)
    {
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
