using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using Scalemon.Common.Updates;

namespace Scalemon.Updater;

/// <summary>Доставка с GitHub и проверка подписанного пакета до остановки приложения.</summary>
public sealed class SignedPackages : IDisposable
{
    private const string Repository = "solve-kz/Scale-monitoring";
    private readonly HttpClient _http = new(new HttpClientHandler { AllowAutoRedirect = true }) { Timeout = TimeSpan.FromMinutes(30) };
    private string? _etag;
    private JsonElement[] _releases = [];
    /// <summary>Создаёт клиент публичного GitHub API без токена участка.</summary>
    public SignedPackages() { _http.DefaultRequestHeaders.UserAgent.ParseAdd("Scalemon-Updater/1.0"); }
    /// <summary>Находит последний подписанный выпуск выбранного канала.</summary>
    public async Task<(ReleaseManifest Manifest, Uri Archive)?> FindAsync(string channel, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{Repository}/releases?per_page=100");
        if (_etag is not null) request.Headers.IfNoneMatch.Add(new EntityTagHeaderValue(_etag));
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (response.StatusCode != HttpStatusCode.NotModified)
        {
            response.EnsureSuccessStatusCode();
            using var json = JsonDocument.Parse(await BoundedAsync(response, 2 * 1024 * 1024, ct));
            _releases = json.RootElement.EnumerateArray().Select(x => x.Clone()).ToArray();
            _etag = response.Headers.ETag?.ToString();
        }
        var ordered = _releases.Where(r => !r.GetProperty("draft").GetBoolean() &&
            (channel == "Test" || !r.GetProperty("prerelease").GetBoolean()))
            .Select(r => (Release: r, Tag: r.GetProperty("tag_name").GetString() ?? ""))
            .Where(r => ValidTag(r.Tag)).OrderByDescending(r => ReleaseVersion.Parse(r.Tag[1..]));
        foreach (var item in ordered)
        {
            var assets = item.Release.GetProperty("assets").EnumerateArray().ToArray();
            var manifestAsset = assets.FirstOrDefault(a => a.GetProperty("name").GetString() == "release-manifest.json");
            var sigAsset = assets.FirstOrDefault(a => a.GetProperty("name").GetString() == "release-manifest.sig");
            if (manifestAsset.ValueKind == JsonValueKind.Undefined || sigAsset.ValueKind == JsonValueKind.Undefined) continue;
            var bytes = await GetBytesAsync(AssetUri(manifestAsset, item.Tag), 65536, ct);
            var signature = await GetBytesAsync(AssetUri(sigAsset, item.Tag), 1024, ct);
            var manifest = VerifyManifest(bytes, signature, PublicKey());
            if (manifest.Version != item.Tag[1..] || manifest.Channel != channel) continue;
            var archive = assets.FirstOrDefault(a => a.GetProperty("name").GetString() == manifest.Archive);
            if (archive.ValueKind == JsonValueKind.Undefined) continue;
            if (string.IsNullOrWhiteSpace(manifest.Notes) && item.Release.TryGetProperty("body", out var body) && body.ValueKind == JsonValueKind.String)
            {
                var notes = body.GetString() ?? "";
                manifest = manifest with { Notes = notes[..Math.Min(notes.Length, 8000)] };
            }
            return (manifest, AssetUri(archive, item.Tag));
        }
        return null;
    }
    private static bool ValidTag(string tag)
    { try { if (!tag.StartsWith('v')) return false; _ = ReleaseVersion.Parse(tag[1..]); return true; } catch { return false; } }
    private static Uri AssetUri(JsonElement asset, string tag)
    {
        var uri = new Uri(asset.GetProperty("browser_download_url").GetString()!);
        if (uri.Scheme != "https" || uri.Host != "github.com" ||
            !uri.AbsolutePath.StartsWith($"/{Repository}/releases/download/{tag}/", StringComparison.Ordinal))
            throw new InvalidDataException("Недопустимый источник пакета.");
        return uri;
    }
    private static string PublicKey()
    {
        var assembly = typeof(SignedPackages).Assembly;
        using var stream = assembly.GetManifestResourceStream("Scalemon.Updater.release-public-key.pem")
            ?? throw new InvalidOperationException("В установщик не встроен ключ подписи выпусков. Требуется выпуск доверенного Setup.");
        using var reader = new StreamReader(stream); return reader.ReadToEnd();
    }
    /// <summary>Проверяет ECDSA-подпись и ограничения манифеста.</summary>
    public static ReleaseManifest VerifyManifest(byte[] bytes, byte[] signature, string publicKey)
    {
        using var key = ECDsa.Create(); key.ImportFromPem(publicKey);
        if (key.KeySize != 256 || !key.VerifyData(bytes, signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
            throw new InvalidDataException("Подпись выпуска недействительна.");
        var manifest = JsonSerializer.Deserialize<ReleaseManifest>(bytes, AtomicJson.Options) ?? throw new InvalidDataException("Пустой манифест.");
        manifest.Validate(); return manifest;
    }
    /// <summary>Загружает архив во временный файл и проверяет длину и SHA-256.</summary>
    public async Task<string> DownloadAsync(ReleaseManifest manifest, Uri uri, Action<long> progress, CancellationToken ct)
    {
        Directory.CreateDirectory(InstallationPaths.Updates);
        var path = Path.Combine(InstallationPaths.Updates, manifest.Archive);
        var part = path + ".part";
        if (File.Exists(path) && await HashMatchesAsync(path, manifest, ct)) return path;
        try
        {
            using var response = await _http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();
            await using var source = await response.Content.ReadAsStreamAsync(ct);
            await using (var target = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true))
            {
                var buffer = new byte[81920]; long total = 0; int read;
                while ((read = await source.ReadAsync(buffer, ct)) != 0)
                {
                    total += read; if (total > manifest.Size) throw new InvalidDataException("Размер загрузки превышен.");
                    await target.WriteAsync(buffer.AsMemory(0, read), ct); progress(total);
                }
                await target.FlushAsync(ct);
                if (total != manifest.Size) throw new InvalidDataException("Загрузка неполна.");
            }
            if (!await HashMatchesAsync(part, manifest, ct)) throw new InvalidDataException("Контрольная сумма пакета не совпадает.");
            File.Move(part, path, true); return path;
        }
        finally { if (File.Exists(part)) File.Delete(part); }
    }
    /// <summary>Сверяет локальный архив с подписанным манифестом.</summary>
    public static async Task<bool> HashMatchesAsync(string path, ReleaseManifest manifest, CancellationToken ct)
    {
        if (new FileInfo(path).Length != manifest.Size) return false;
        await using var file = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(file, ct)).Equals(manifest.Sha256, StringComparison.OrdinalIgnoreCase);
    }
    /// <summary>Безопасно распаковывает полный кандидат без ссылок и выхода за каталог.</summary>
    public static void Extract(string archivePath, string directory, ReleaseManifest manifest)
    {
        if (Directory.Exists(directory)) throw new IOException("Каталог кандидата уже существует.");
        Directory.CreateDirectory(directory);
        var root = Path.GetFullPath(directory) + Path.DirectorySeparatorChar;
        using var zip = ZipFile.OpenRead(archivePath);
        long total = 0; var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (zip.Entries.Count > 20000) throw new InvalidDataException("Слишком много файлов.");
        foreach (var entry in zip.Entries)
        {
            var name = entry.FullName.Replace('\\', '/');
            var mode = (entry.ExternalAttributes >> 16) & 0xF000;
            if (mode is 0xA000 || (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0 ||
                name.StartsWith('/') || name.Contains(':') || name.Split('/').Any(p => p is "." or ".." || p.EndsWith(' ') || p.EndsWith('.')))
                throw new InvalidDataException("Небезопасный путь в архиве.");
            var full = Path.GetFullPath(Path.Combine(directory, name));
            if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !names.Add(full)) throw new InvalidDataException("Повторный или внешний путь.");
            total = checked(total + entry.Length);
            if (total > manifest.ExpandedSize) throw new InvalidDataException("Превышен размер распаковки.");
            if (name.EndsWith('/')) { Directory.CreateDirectory(full); continue; }
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            using var source = entry.Open(); using var target = new FileStream(full, FileMode.CreateNew);
            var buffer = new byte[81920]; long written = 0; int read;
            while ((read = source.Read(buffer)) > 0)
            { written += read; if (written > entry.Length) throw new InvalidDataException("Превышен размер файла."); target.Write(buffer, 0, read); }
            if (written != entry.Length) throw new InvalidDataException("Неполный файл.");
        }
        if (!File.Exists(Path.Combine(directory, "Scalemon.ServiceHost.exe")) || !Directory.Exists(Path.Combine(directory, "wwwroot")))
            throw new InvalidDataException("В архиве нет полного приложения.");
    }
    private async Task<byte[]> GetBytesAsync(Uri uri, int limit, CancellationToken ct)
    { using var response = await _http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct); response.EnsureSuccessStatusCode(); return await BoundedAsync(response, limit, ct); }
    private static async Task<byte[]> BoundedAsync(HttpResponseMessage response, int limit, CancellationToken ct)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(ct); using var result = new MemoryStream();
        var buffer = new byte[8192]; int read;
        while ((read = await stream.ReadAsync(buffer, ct)) > 0)
        { if (result.Length + read > limit) throw new InvalidDataException("Ответ слишком велик."); result.Write(buffer, 0, read); }
        return result.ToArray();
    }
    /// <inheritdoc />
    public void Dispose() => _http.Dispose();
}
