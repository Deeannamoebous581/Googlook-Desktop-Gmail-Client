using System;
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
            // Allow-list, not deny-list: anything that isn't an inline (cid:/data:)
            // reference is stripped. A deny-list on "http://"/"https://" misses
            // "HTTPS://", " https://" (leading whitespace), and protocol-relative
            // "//tracker.example" — all of which browsers happily fetch.
            s.PostProcessNode += (_, e) =>
            {
                if (e.Node is not IElement el) return;

                foreach (var attr in RemoteCapableAttributes)
                {
                    var value = el.GetAttribute(attr);
                    if (value is null || IsInlineRef(value)) continue;
                    if (attr == "src") el.SetAttribute("src", ""); // keep the tag, blank the fetch
                    else el.RemoveAttribute(attr);
                }

                // Inline-style beacons: background-image:url(https://...) phones home
                // without any src attribute. Styles come back on "Show images".
                var style = el.GetAttribute("style");
                if (style is not null && style.Contains("url", StringComparison.OrdinalIgnoreCase))
                    el.RemoveAttribute("style");
            };
        }
        return s;
    }

    private static readonly string[] RemoteCapableAttributes =
        { "src", "srcset", "poster", "background", "formaction" };

    private static bool IsInlineRef(string value)
    {
        var v = value.TrimStart();
        return v.StartsWith("cid:", StringComparison.OrdinalIgnoreCase) ||
               v.StartsWith("data:", StringComparison.OrdinalIgnoreCase);
    }
}
