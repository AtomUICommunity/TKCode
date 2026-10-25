using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;

namespace TKCode.Desktop.Updates;

/// <summary>
/// 下载安装程序到暂存目录（auto-update.md §6）：先写 &lt;name&gt;.partial，已存在时用 Range 续传（服务器回 200 则从头下载），
/// 完成后校验大小与 SHA-256，通过才改名为正式文件名。
/// </summary>
public sealed class UpdateDownloader(HttpClient http)
{
    private const int BufferSize = 81920;

    /// <summary>下载并校验，返回安装程序的完整路径；目标文件已存在且校验通过时直接返回。</summary>
    public async Task<string> DownloadAsync(string url, string directory, UpdateAsset asset, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(directory);
        var target = Path.Combine(directory, Path.GetFileName(asset.Name));
        if (File.Exists(target))
        {
            if (await MatchesAsync(target, asset, cancellationToken).ConfigureAwait(false))
            {
                progress?.Report(1);
                return target;
            }
            File.Delete(target);
        }
        var partial = target + ".partial";
        var offset = File.Exists(partial) ? new FileInfo(partial).Length : 0;
        if (offset > asset.Size)
        {
            File.Delete(partial);
            offset = 0;
        }

        if (offset < asset.Size)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            if (offset > 0)
            {
                request.Headers.Range = new RangeHeaderValue(offset, null);
            }
            HttpResponseMessage response;
            try
            {
                response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            }
            catch (HttpRequestException ex)
            {
                throw new UpdateFeedException(ex.Message, ex);
            }
            using (response)
            {
                if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
                {
                    File.Delete(partial);
                    throw new UpdateFeedException("The server rejected the resume request; the download will restart.");
                }
                if (!response.IsSuccessStatusCode)
                {
                    throw new UpdateFeedException($"Download returned {(int)response.StatusCode} {response.ReasonPhrase}.");
                }
                // 不支持 Range 的服务器返回完整内容：从头写
                var append = offset > 0 && response.StatusCode == HttpStatusCode.PartialContent;
                if (!append)
                {
                    offset = 0;
                }
                await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                await using var file = new FileStream(partial, append ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, BufferSize, useAsync: true);
                var buffer = new byte[BufferSize];
                var written = offset;
                progress?.Report(asset.Size == 0 ? 0 : (double)written / asset.Size);
                // 每读一块（80KB）都上报的话，快网速下每秒几百次，每次都在 UI 线程刷新卡片文字：只在千分比变化时上报
                var reported = asset.Size == 0 ? 0 : written * 1000 / asset.Size;
                int read;
                try
                {
                    while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                    {
                        await file.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                        written += read;
                        if (written > asset.Size)
                        {
                            break;
                        }
                        var permille = written * 1000 / asset.Size;
                        if (permille != reported || written == asset.Size)
                        {
                            reported = permille;
                            progress?.Report((double)written / asset.Size);
                        }
                    }
                }
                catch (Exception ex) when (ex is HttpRequestException or IOException && !cancellationToken.IsCancellationRequested)
                {
                    // 已写入的部分保留，下次续传
                    throw new UpdateFeedException(ex.Message, ex);
                }
            }
        }

        if (!await MatchesAsync(partial, asset, cancellationToken).ConfigureAwait(false))
        {
            File.Delete(partial);
            throw new UpdateFeedException($"{asset.Name} failed verification (size or SHA-256 mismatch).");
        }
        File.Move(partial, target, overwrite: true);
        progress?.Report(1);
        return target;
    }

    /// <summary>大小与 SHA-256 都与清单一致。</summary>
    public static async Task<bool> MatchesAsync(string path, UpdateAsset asset, CancellationToken cancellationToken)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length != asset.Size)
        {
            return false;
        }
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, useAsync: true);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexStringLower(hash) == asset.Sha256.ToLowerInvariant();
    }
}
