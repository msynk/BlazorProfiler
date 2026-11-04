using System.Text;

namespace BlazorProfiler.Pipeline;

/// <summary>
/// Removes or masks potentially sensitive data before events are stored or exported.
/// </summary>
/// <remarks>Implementations must be deterministic and must never throw for malformed input.</remarks>
public interface IProfilerRedactionPolicy
{
    /// <summary>Returns a sanitized representation of <paramref name="url"/> suitable for storage and display.</summary>
    string SanitizeUrl(string url);

    /// <summary>
    /// Returns a stable key that groups requests to the same endpoint (method, host, templated path), without query values.
    /// </summary>
    string GetEndpointKey(string? method, string url);

    /// <summary>Returns a sanitized JavaScript interop identifier.</summary>
    string SanitizeIdentifier(string identifier);
}

/// <summary>
/// Default redaction policy: strips user information and fragments, masks query values (keeping keys), and replaces
/// identifier-like path segments with placeholders.
/// </summary>
public sealed class DefaultRedactionPolicy : IProfilerRedactionPolicy
{
    /// <summary>The placeholder used for redacted values.</summary>
    public const string RedactedValue = "[redacted]";

    private static readonly string[] s_sensitiveQueryKeyFragments =
    [
        "token", "secret", "password", "passwd", "pwd", "key", "sig", "signature", "auth", "code", "session",
        "jwt", "credential", "otp", "nonce", "state", "ticket", "assertion", "cookie", "email", "phone", "ssn",
    ];

    private readonly RedactionOptions _options;

    /// <summary>Creates the policy.</summary>
    public DefaultRedactionPolicy(RedactionOptions options) => _options = options;

    /// <inheritdoc />
    public string SanitizeUrl(string url)
    {
        if (string.IsNullOrEmpty(url))
        {
            return string.Empty;
        }

        try
        {
            SplitUrl(url, out var origin, out var path, out var query);
            var builder = new StringBuilder(url.Length);
            builder.Append(origin);
            AppendPath(builder, path);
            if (query.Length > 0)
            {
                builder.Append('?');
                AppendQuery(builder, query);
            }

            return builder.ToString();
        }
        catch (Exception)
        {
            return RedactedValue;
        }
    }

    /// <inheritdoc />
    public string GetEndpointKey(string? method, string url)
    {
        if (string.IsNullOrEmpty(url))
        {
            return method ?? string.Empty;
        }

        try
        {
            SplitUrl(url, out var origin, out var path, out _);
            var builder = new StringBuilder(path.Length + origin.Length + 8);
            if (!string.IsNullOrEmpty(method))
            {
                builder.Append(method.ToUpperInvariant()).Append(' ');
            }

            builder.Append(origin);
            AppendPath(builder, path, forceTemplate: true);
            return builder.ToString();
        }
        catch (Exception)
        {
            return method ?? string.Empty;
        }
    }

    /// <inheritdoc />
    public string SanitizeIdentifier(string identifier)
    {
        if (string.IsNullOrEmpty(identifier))
        {
            return string.Empty;
        }

        foreach (var prefix in _options.RedactedJSIdentifierPrefixes)
        {
            if (identifier.StartsWith(prefix, StringComparison.Ordinal))
            {
                return RedactedValue;
            }
        }

        return identifier.Length > 256 ? identifier[..256] : identifier;
    }

    private static void SplitUrl(string url, out string origin, out string path, out string query)
    {
        var fragmentIndex = url.IndexOf('#', StringComparison.Ordinal);
        if (fragmentIndex >= 0)
        {
            url = url[..fragmentIndex];
        }

        var queryIndex = url.IndexOf('?', StringComparison.Ordinal);
        query = queryIndex >= 0 ? url[(queryIndex + 1)..] : string.Empty;
        var withoutQuery = queryIndex >= 0 ? url[..queryIndex] : url;

        var schemeIndex = withoutQuery.IndexOf("://", StringComparison.Ordinal);
        if (schemeIndex > 0)
        {
            var authorityStart = schemeIndex + 3;
            var pathStart = withoutQuery.IndexOf('/', authorityStart);
            var authority = pathStart >= 0 ? withoutQuery[authorityStart..pathStart] : withoutQuery[authorityStart..];
            var at = authority.LastIndexOf('@');
            if (at >= 0)
            {
                // Strip user information (user:password@host).
                authority = authority[(at + 1)..];
            }

            origin = string.Concat(withoutQuery.AsSpan(0, authorityStart), authority).ToLowerInvariant();
            path = pathStart >= 0 ? withoutQuery[pathStart..] : "/";
        }
        else
        {
            origin = string.Empty;
            path = withoutQuery;
        }
    }

    private void AppendPath(StringBuilder builder, string path, bool forceTemplate = false)
    {
        if (!_options.TemplatePathIdentifiers && !forceTemplate)
        {
            builder.Append(path);
            return;
        }

        var segments = path.Split('/');
        for (var i = 0; i < segments.Length; i++)
        {
            if (i > 0)
            {
                builder.Append('/');
            }

            builder.Append(TemplateSegment(segments[i]));
        }
    }

    private void AppendQuery(StringBuilder builder, string query)
    {
        var first = true;
        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!first)
            {
                builder.Append('&');
            }

            first = false;
            var equals = pair.IndexOf('=', StringComparison.Ordinal);
            var key = equals >= 0 ? pair[..equals] : pair;
            var value = equals >= 0 ? pair[(equals + 1)..] : null;
            var decodedKey = SafeUnescape(key);
            builder.Append(key);
            if (value is null)
            {
                continue;
            }

            builder.Append('=');
            var keep = !IsSensitiveKey(decodedKey)
                && (!_options.RedactQueryValues || _options.AllowedQueryParameters.Contains(decodedKey));
            builder.Append(keep ? value : RedactedValue);
        }
    }

    private static bool IsSensitiveKey(string key)
    {
        foreach (var fragment in s_sensitiveQueryKeyFragments)
        {
            if (key.Contains(fragment, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static string SafeUnescape(string value)
    {
        try
        {
            return Uri.UnescapeDataString(value);
        }
        catch (Exception)
        {
            return value;
        }
    }

    /// <summary>Replaces identifier-like path segments with placeholders.</summary>
    internal static string TemplateSegment(string segment)
    {
        if (segment.Length == 0)
        {
            return segment;
        }

        if (segment.Contains('@', StringComparison.Ordinal) || segment.Contains("%40", StringComparison.OrdinalIgnoreCase))
        {
            return "{email}";
        }

        if (Guid.TryParse(segment, out _))
        {
            return "{guid}";
        }

        var digits = 0;
        var hex = 0;
        var tokenChars = 0;
        var letters = 0;
        foreach (var c in segment)
        {
            if (char.IsAsciiDigit(c))
            {
                digits++;
                hex++;
                tokenChars++;
            }
            else if (char.IsAsciiHexDigit(c))
            {
                hex++;
                tokenChars++;
                letters++;
            }
            else if (char.IsAsciiLetter(c))
            {
                tokenChars++;
                letters++;
            }
            else if (c is '-' or '_' or '.' or '~' or '=' or '%')
            {
                tokenChars++;
            }
        }

        if (digits == segment.Length)
        {
            return "{id}";
        }

        if (hex == segment.Length && segment.Length >= 16)
        {
            return "{hex}";
        }

        // Long opaque tokens containing a mix of letters and digits (for example base64url values).
        if (tokenChars == segment.Length && segment.Length >= 24 && digits > 0 && letters > 0)
        {
            return "{token}";
        }

        // Mixed identifiers such as "order-12345" keep their textual prefix.
        if (digits >= 4 && digits * 2 >= segment.Length)
        {
            return "{id}";
        }

        return segment;
    }
}
