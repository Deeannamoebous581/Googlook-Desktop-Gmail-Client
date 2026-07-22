using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Drive.v3;
using Google.Apis.Services;
using Googlook.Models;

namespace Googlook.Services;

public sealed class DriveFileInfo
{
    public string Id       { get; set; } = "";
    public string Name     { get; set; } = "";
    public string MimeType { get; set; } = "";
    public long   Size     { get; set; }
    public bool   IsGoogleDoc => MimeType.StartsWith("application/vnd.google-apps", StringComparison.Ordinal);
}

/// <summary>
/// Reads Google Drive with the account's existing OAuth (drive.readonly) so Compose
/// can attach a file straight from Drive. Google-native docs (Docs/Sheets/Slides) are
/// exported to a portable format on download; ordinary files download as-is.
/// </summary>
public sealed class DriveClient : IDisposable
{
    private readonly DriveService _svc;

    public DriveClient(UserCredential credential, string appName = "Googlook")
    {
        _svc = new DriveService(new BaseClientService.Initializer
        {
            HttpClientInitializer = credential,
            ApplicationName = appName,
        });
    }

    public async Task<List<DriveFileInfo>> ListAsync(string? search, int pageSize = 25)
    {
        var req = _svc.Files.List();
        req.PageSize = pageSize;
        req.Fields = "files(id,name,mimeType,size,modifiedTime)";
        req.OrderBy = "modifiedTime desc";
        req.Q = string.IsNullOrWhiteSpace(search)
            ? "trashed=false"
            : $"trashed=false and name contains '{Escape(search!)}'";
        req.SupportsAllDrives = true;
        req.IncludeItemsFromAllDrives = true;

        var resp = await req.ExecuteAsync();
        var list = new List<DriveFileInfo>();
        if (resp.Files is not null)
            foreach (var f in resp.Files)
                list.Add(new DriveFileInfo
                {
                    Id = f.Id, Name = f.Name, MimeType = f.MimeType ?? "",
                    Size = (long)(f.Size ?? 0),
                });
        return list;
    }

    /// <summary>Downloads a Drive file's bytes as an attachment ready to send.</summary>
    public async Task<OutgoingAttachment> DownloadAsync(DriveFileInfo file)
    {
        using var ms = new MemoryStream();
        string name = file.Name;
        string mime = string.IsNullOrEmpty(file.MimeType) ? "application/octet-stream" : file.MimeType;

        if (file.IsGoogleDoc)
        {
            var (exportMime, ext) = ExportFor(file.MimeType);
            await _svc.Files.Export(file.Id, exportMime).DownloadAsync(ms);
            mime = exportMime;
            if (!name.EndsWith(ext, StringComparison.OrdinalIgnoreCase)) name += ext;
        }
        else
        {
            await _svc.Files.Get(file.Id).DownloadAsync(ms);
        }

        return new OutgoingAttachment { Filename = name, MimeType = mime, Data = ms.ToArray(), Source = "Drive" };
    }

    private static (string mime, string ext) ExportFor(string googleMime) => googleMime switch
    {
        "application/vnd.google-apps.document" => ("application/pdf", ".pdf"),
        "application/vnd.google-apps.spreadsheet" =>
            ("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", ".xlsx"),
        "application/vnd.google-apps.presentation" => ("application/pdf", ".pdf"),
        "application/vnd.google-apps.drawing" => ("image/png", ".png"),
        _ => ("application/pdf", ".pdf"),
    };

    private static string Escape(string s) => s.Replace("\\", "\\\\").Replace("'", "\\'");

    public void Dispose() => _svc.Dispose();
}
