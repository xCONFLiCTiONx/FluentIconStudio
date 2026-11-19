using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace FluentIconStudio.Services;

public static class FluentIconDownloader
{
    private const string GitHubZipUrl = "https://github.com/microsoft/fluentui-system-icons/archive/refs/heads/main.zip";

    public static string PackDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "FluentIconStudio", "FluentIconPack");

    public static bool IsPackDownloaded()
    {
        if (!Directory.Exists(PackDirectory))
            return false;

        try
        {
            return Directory.EnumerateFiles(PackDirectory, "*.svg", SearchOption.AllDirectories).Any();
        }
        catch
        {
            return false;
        }
    }

    public static async Task DownloadAndExtractPackAsync(IProgress<string> progress, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(PackDirectory);
        var tempZip = Path.Combine(Path.GetTempPath(), $"fluentui-icons-{Guid.NewGuid():N}.zip");

        try
        {
            progress.Report("Connecting to GitHub (microsoft/fluentui-system-icons)...");
            using var client = new HttpClient();
            client.DefaultRequestHeaders.UserAgent.ParseAdd("FluentIconStudio/1.0 (WPF; Windows)");
            client.Timeout = TimeSpan.FromMinutes(15);

            using (var response = await client.GetAsync(GitHubZipUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken))
            {
                response.EnsureSuccessStatusCode();
                var contentLength = response.Content.Headers.ContentLength;

                await using var contentStream = await response.Content.ReadAsStreamAsync(cancellationToken);
                await using var fileStream = new FileStream(tempZip, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);

                var buffer = new byte[81920];
                long totalBytesRead = 0;
                int bytesRead;

                while ((bytesRead = await contentStream.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
                    totalBytesRead += bytesRead;

                    if (contentLength.HasValue && contentLength.Value > 0)
                    {
                        var megabytesRead = totalBytesRead / (1024.0 * 1024.0);
                        var totalMegabytes = contentLength.Value / (1024.0 * 1024.0);
                        progress.Report($"Downloading Fluent Icon Pack from GitHub... {megabytesRead:F1} MB / {totalMegabytes:F1} MB");
                    }
                    else
                    {
                        var megabytesRead = totalBytesRead / (1024.0 * 1024.0);
                        progress.Report($"Downloading Fluent Icon Pack from GitHub... {megabytesRead:F1} MB");
                    }
                }
            }

            progress.Report("Extracting SVG icons from package archive...");
            await Task.Run(() =>
            {
                if (Directory.Exists(PackDirectory))
                {
                    try
                    {
                        Directory.Delete(PackDirectory, true);
                    }
                    catch { }
                }
                Directory.CreateDirectory(PackDirectory);

                ZipFile.ExtractToDirectory(tempZip, PackDirectory, overwriteFiles: true);
                progress.Report("Extraction complete!");
            }, cancellationToken);
        }
        finally
        {
            if (File.Exists(tempZip))
            {
                try { File.Delete(tempZip); } catch { }
            }
        }
    }
}
