using System.Globalization;
using Microsoft.AspNetCore.Http;
using Microsoft.Net.Http.Headers;

namespace Claudalisk.AspNetCore;

public static class Versions
{
    public static string ETagFor(long version) => $"\"{version.ToString(CultureInfo.InvariantCulture)}\"";

    public static void SetETag(this HttpResponse response, long version)
    {
        ArgumentNullException.ThrowIfNull(response);
        response.Headers.ETag = ETagFor(version);
    }

    public static long RequiredIfMatchVersion(this HttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var header = request.Headers.IfMatch.ToString();
        if (string.IsNullOrWhiteSpace(header))
        {
            throw new PreconditionRequiredException();
        }

        if (!EntityTagHeaderValue.TryParse(header.Trim(), out var tag)
            || tag.IsWeak
            || !long.TryParse(tag.Tag.ToString().Trim('"'), NumberStyles.None, CultureInfo.InvariantCulture, out var version)
            || version < 1)
        {
            throw new BadHttpRequestException($"If-Match must be one strong version tag, such as \"7\"; got {header}.");
        }

        return version;
    }
}
