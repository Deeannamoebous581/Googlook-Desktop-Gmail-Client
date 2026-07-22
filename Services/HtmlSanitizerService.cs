using AngleSharp.Dom;
using Ganss.Xss;

namespace Googlook.Services;

/// <summary>
/// Neutralises in-email trackers before the body is rendered:
///  - removes &lt;script&gt;, inline event handlers, and dangerous protocols (default in Ganss),
///  - when blockRemote is on, strips remote img/iframe/background sources so tracking
///    pixels and beacons never phone home — Thunderbird's "remote content blocked".
/// Inline (cid:) and data: images are still allowed so legitimate embedded art works.
/// </summary>
public sealed class HtmlSanitizerService
{
    private readonly HtmlSanitizer _blocked = Build(blockRemote: true);
    private readonly HtmlSanitizer _permissive = Build(blockRemote: false);

    public string Sanitize(string html, bool blockRemote) =>
        (blockRemote ? _blocked : _permissive).Sanitize(html ?? string.Empty);

    private static HtmlSanitizer Build(bool blockRemote)
    {
        var s = new HtmlSanitizer();
        s.AllowedSchemes.Add("cid");   // inline attachment references
        s.AllowedSchemes.Add("data");  // embedded base64 images

        if (blockRemote)
        {
            s.PostProcessNode += (_, e) =>
            {
                if (e.Node is not IElement el) return;

                var src = el.GetAttribute("src");
                if (src is not null &&
                    (src.StartsWith("http://") || src.StartsWith("https://")))
                    el.SetAttribute("src", "");   // blank remote images/iframes

                if (el.HasAttribute("background"))
                    el.RemoveAttribute("background"); // CSS background beacons
            };
        }
        return s;
    }
}
