namespace CmdWorkspace.Term;

/// <summary>
/// Opens a link from terminal output in the default browser, after strict checks.
/// Only http and https, no spaces or control characters, at most 2048 characters. The text never goes to a shell.
/// It is only called after the user clicked the link.
/// </summary>
public static class LinkOpener
{
    public static bool IsSafe(string text, out Uri uri)
    {
        uri = null!;
        if (string.IsNullOrWhiteSpace(text) || text.Length > 2048) return false;
        if (text.Any(char.IsControl) || text.Any(char.IsWhiteSpace)) return false;
        if (!Uri.TryCreate(text, UriKind.Absolute, out var parsed)) return false;
        if (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps) return false;
        if (string.IsNullOrEmpty(parsed.Host)) return false;
        if (!string.IsNullOrEmpty(parsed.UserInfo)) return false;   // Phase 7: "http://good.com@bad.com" style links are refused
        uri = parsed;
        return true;
    }

    /// <summary>Returns null when it worked, otherwise a short message.</summary>
    public static async Task<string?> OpenAsync(string text)
    {
        if (!IsSafe(text, out var uri)) return "This link was not opened. Only plain http and https links are allowed (no user name or password in the link).";
        try
        {
            var ok = await Windows.System.Launcher.LaunchUriAsync(uri);
            return ok ? null : "Windows could not open the link in a browser.";
        }
        catch (Exception)
        {
            return "Windows could not open the link in a browser.";
        }
    }
}
