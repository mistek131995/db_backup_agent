using System.Net;
using System.Net.Http.Headers;
using System.Text;
using BackupsterAgent.Configuration;

namespace BackupsterAgent.Providers.Upload;

public sealed class WebDavUploadProvider : IUploadProvider, IDisposable
{
    private static readonly HttpMethod Mkcol = new("MKCOL");
    private const int CopyBufferSize = 65536;

    private readonly WebDavSettings _settings;
    private readonly ILogger<WebDavUploadProvider> _logger;
    private readonly HttpClient _http;
    private readonly Uri _baseUri;
    private readonly string _basePath;

    public WebDavUploadProvider(WebDavSettings settings, ILogger<WebDavUploadProvider> logger)
    {
        _settings = settings;
        _logger = logger;

        if (string.IsNullOrWhiteSpace(_settings.BaseUrl))
            throw new InvalidOperationException("WebDavSettings.BaseUrl не задан.");

        if (!Uri.TryCreate(_settings.BaseUrl, UriKind.Absolute, out var parsed))
            throw new InvalidOperationException($"WebDavSettings.BaseUrl '{_settings.BaseUrl}' не является корректным URL.");

        if (parsed.Scheme != Uri.UriSchemeHttps && parsed.Scheme != Uri.UriSchemeHttp)
            throw new InvalidOperationException(
                $"WebDavSettings.BaseUrl должен использовать схему http или https (получено: {parsed.Scheme}).");

        if (parsed.Scheme == Uri.UriSchemeHttp)
            _logger.LogWarning(
                "WebDAV BaseUrl uses plain http for {Host} — credentials and backup data will travel unencrypted. Use https in production.",
                parsed.Host);

        _baseUri = new Uri(parsed.GetLeftPart(UriPartial.Authority) + "/", UriKind.Absolute);
        _basePath = NormalizeBasePath(parsed.AbsolutePath, _settings.RemotePath);

        _http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
        {
            BaseAddress = _baseUri,
            Timeout = Timeout.InfiniteTimeSpan,
        };

        var creds = Convert.ToBase64String(
            Encoding.UTF8.GetBytes($"{_settings.Username}:{_settings.Password}"));
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", creds);
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("BackupsterAgent/1.0");
    }

    public void Dispose() => _http.Dispose();

    public async Task<string> UploadAsync(string filePath, string folder, IProgress<long>? progress, CancellationToken ct)
    {
        var fileName = Path.GetFileName(filePath);
        var remoteDir = JoinPath(_basePath, folder);
        var remotePath = JoinPath(remoteDir, fileName);
        var fileSize = new FileInfo(filePath).Length;

        _logger.LogInformation(
            "WebDAV uploading '{FilePath}' → {Host}{RemotePath} ({Size} bytes)",
            filePath, _baseUri.Host, remotePath, fileSize);

        await EnsureRemoteDirectoryAsync(remoteDir, ct);

        await using (var fileStream = new FileStream(
            filePath, FileMode.Open, FileAccess.Read, FileShare.Read, CopyBufferSize, useAsync: true))
        await using (var progressStream = new ProgressReadStream(fileStream, progress))
        {
            using var content = new StreamContent(progressStream, CopyBufferSize);
            content.Headers.ContentLength = fileSize;
            content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");

            using var request = new HttpRequestMessage(HttpMethod.Put, BuildUri(remotePath))
            {
                Content = content,
            };

            using var response = await SendAsync(request, HttpCompletionOption.ResponseContentRead, ct);
            await EnsureSuccessAsync(response, "PUT", remotePath, ct);
        }

        var storagePath = $"webdav://{_baseUri.Host}{remotePath}";
        _logger.LogInformation("WebDAV upload completed. StoragePath: '{StoragePath}'", storagePath);
        return storagePath;
    }

    public Task UploadBytesAsync(byte[] content, string objectKey, CancellationToken ct) =>
        throw new NotSupportedException("Byte-array upload is not supported for WebDAV provider. File backup with deduplication is S3/AzureBlob-only.");

    public Task<bool> ExistsAsync(string objectKey, CancellationToken ct) =>
        throw new NotSupportedException("ExistsAsync is not supported for WebDAV provider. File backup with deduplication is S3/AzureBlob-only.");

    public async Task<long> GetObjectSizeAsync(string objectKey, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(objectKey);

        var remotePath = JoinPath(_basePath, objectKey);

        using var request = new HttpRequestMessage(HttpMethod.Head, BuildUri(remotePath));
        using var response = await SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);

        if (response.StatusCode == HttpStatusCode.NotFound)
            throw new FileNotFoundException(
                $"WebDAV-файл '{remotePath}' не найден на хосте {_baseUri.Host}.", remotePath);

        await EnsureSuccessAsync(response, "HEAD", remotePath, ct);

        return response.Content.Headers.ContentLength
            ?? throw new IOException(
                $"WebDAV-сервер {_baseUri.Host} не вернул Content-Length для '{remotePath}'.");
    }

    public async Task DownloadAsync(string objectKey, string localPath, IProgress<long>? progress, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(objectKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(localPath);

        var remotePath = JoinPath(_basePath, objectKey);
        var tmpPath = localPath + ".download-tmp";

        _logger.LogInformation(
            "WebDAV downloading {Host}{RemotePath} → '{LocalPath}'",
            _baseUri.Host, remotePath, localPath);

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, BuildUri(remotePath));
            using var response = await SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);

            if (response.StatusCode == HttpStatusCode.NotFound)
                throw new FileNotFoundException(
                    $"WebDAV-файл '{remotePath}' не найден на хосте {_baseUri.Host}.", remotePath);

            await EnsureSuccessAsync(response, "GET", remotePath, ct);

            await using (var remote = await response.Content.ReadAsStreamAsync(ct))
            await using (var file = new FileStream(
                tmpPath, FileMode.Create, FileAccess.Write, FileShare.None, CopyBufferSize, useAsync: true))
            {
                await CopyWithProgressAsync(remote, file, progress, ct);
            }

            File.Move(tmpPath, localPath, overwrite: true);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            SafeDeleteTmp(tmpPath);
            throw;
        }
        catch
        {
            SafeDeleteTmp(tmpPath);
            throw;
        }

        _logger.LogInformation("WebDAV download completed: '{LocalPath}'", localPath);
    }

    public Task<byte[]> DownloadBytesAsync(string objectKey, CancellationToken ct) =>
        throw new NotSupportedException("DownloadBytesAsync is not supported for WebDAV provider. File backup and chunk operations are S3/AzureBlob-only.");

    public IAsyncEnumerable<StorageObject> ListAsync(string prefix, CancellationToken ct) =>
        throw new NotSupportedException("ListAsync is not supported for WebDAV provider. Chunk GC is S3/AzureBlob-only.");

    public async Task DeleteAsync(string objectKey, CancellationToken ct)
    {
        var remotePath = JoinPath(_basePath, objectKey);

        _logger.LogInformation("WebDAV deleting {Host}{RemotePath}", _baseUri.Host, remotePath);

        using var request = new HttpRequestMessage(HttpMethod.Delete, BuildUri(remotePath));
        using var response = await SendAsync(request, HttpCompletionOption.ResponseContentRead, ct);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            _logger.LogDebug(
                "WebDAV DeleteAsync: {RemotePath} not found — treating as already-deleted", remotePath);
            return;
        }

        await EnsureSuccessAsync(response, "DELETE", remotePath, ct);

        _logger.LogInformation("WebDAV delete completed. RemotePath: '{RemotePath}'", remotePath);
    }

    private async Task EnsureRemoteDirectoryAsync(string remoteDir, CancellationToken ct)
    {
        var parts = remoteDir.TrimStart('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        var current = string.Empty;

        foreach (var part in parts)
        {
            ct.ThrowIfCancellationRequested();
            current += "/" + part;

            using var request = new HttpRequestMessage(Mkcol, BuildUri(current));
            using var response = await SendAsync(request, HttpCompletionOption.ResponseContentRead, ct);

            if (response.IsSuccessStatusCode)
                continue;

            if (response.StatusCode == HttpStatusCode.MethodNotAllowed)
                continue;

            await EnsureSuccessAsync(response, "MKCOL", current, ct);
        }
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, HttpCompletionOption completion, CancellationToken ct)
    {
        try
        {
            return await _http.SendAsync(request, completion, ct);
        }
        catch (HttpRequestException ex)
        {
            throw new IOException(
                $"Не удалось подключиться к WebDAV-серверу {_baseUri.Host}. Проверьте сетевой доступ и BaseUrl.", ex);
        }
    }

    private async Task EnsureSuccessAsync(HttpResponseMessage response, string verb, string remotePath, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
            return;

        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            await DiscardBodyAsync(response, ct);
            throw new UnauthorizedAccessException(
                $"Не удалось аутентифицироваться на WebDAV-сервере {_baseUri.Host}. " +
                "Проверьте логин/пароль в настройках хранилища (для Яндекс.Диска используйте пароль приложения).");
        }

        var body = await ReadShortBodyAsync(response, ct);
        throw new IOException(
            $"WebDAV {verb} {remotePath} вернул {(int)response.StatusCode} {response.ReasonPhrase} от {_baseUri.Host}." +
            (string.IsNullOrEmpty(body) ? "" : $" Тело ответа: {body}"));
    }

    private static async Task DiscardBodyAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            await response.Content.ReadAsByteArrayAsync(ct);
        }
        catch
        {
        }
    }

    private static async Task<string> ReadShortBodyAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            var raw = await response.Content.ReadAsStringAsync(ct);
            if (string.IsNullOrEmpty(raw))
                return string.Empty;
            return raw.Length > 512 ? raw[..512] : raw;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static async Task CopyWithProgressAsync(Stream source, Stream destination, IProgress<long>? progress, CancellationToken ct)
    {
        var buffer = new byte[CopyBufferSize];
        long total = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, ct)) > 0)
        {
            await destination.WriteAsync(buffer.AsMemory(0, read), ct);
            total += read;
            progress?.Report(total);
        }
    }

    private void SafeDeleteTmp(string tmpPath)
    {
        if (!File.Exists(tmpPath)) return;
        try { File.Delete(tmpPath); }
        catch (Exception ex) { _logger.LogDebug(ex, "Failed to delete WebDAV download tmp file '{TmpPath}'", tmpPath); }
    }

    private Uri BuildUri(string absolutePath)
    {
        var encoded = string.Join('/', absolutePath
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(Uri.EscapeDataString));
        return new Uri(_baseUri, "/" + encoded);
    }

    private static string JoinPath(string left, string right)
    {
        var l = (left ?? string.Empty).TrimEnd('/');
        var r = (right ?? string.Empty).Trim('/');
        if (l.Length == 0 && r.Length == 0) return "/";
        if (r.Length == 0) return l.Length == 0 ? "/" : l;
        return (l.Length == 0 ? string.Empty : l) + "/" + r;
    }

    private static string NormalizeBasePath(string baseUriPath, string remotePath)
    {
        var combined = JoinPath(baseUriPath ?? string.Empty, remotePath ?? string.Empty);
        return combined.Length == 0 ? "/" : combined;
    }
}
