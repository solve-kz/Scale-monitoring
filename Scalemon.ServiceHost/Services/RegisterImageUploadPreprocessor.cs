using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using Microsoft.Extensions.Options;
using Scalemon.WebApp.Models;

namespace Scalemon.ServiceHost.Services;

/// <summary>Подготовленное портретное изображение для файлового проекта сверки.</summary>
public sealed record PreparedRegisterUpload(string FileName, MemoryStream Content)
    : IAsyncDisposable
{
    public long Length => Content.Length;

    public ValueTask DisposeAsync() => Content.DisposeAsync();
}

/// <summary>Нормализует ориентацию загруженных JPG/PNG до калибровки.</summary>
public interface IRegisterImageUploadPreprocessor
{
    /// <summary>Проверяет лимит и возвращает физически ориентированные PNG-файлы.</summary>
    Task<IReadOnlyList<PreparedRegisterUpload>> PrepareAsync(
        IReadOnlyList<IFormFile> files,
        CancellationToken cancellationToken = default);
}

/// <summary>Применяет EXIF-ориентацию и поворачивает альбомный лист в портрет.</summary>
public sealed class RegisterImageUploadPreprocessor : IRegisterImageUploadPreprocessor
{
    private const int ExifOrientationId = 0x0112;
    private const long MaxImagePixels = 20_000_000;
    private readonly long _maxUploadBytes;

    public RegisterImageUploadPreprocessor(IOptions<WeightRegisterReviewOptions> options)
    {
        _maxUploadBytes = Math.Max(1, options.Value.MaxUploadBytes);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<PreparedRegisterUpload>> PrepareAsync(
        IReadOnlyList<IFormFile> files,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(files);
        if (files.Count == 0)
        {
            throw new InvalidOperationException("Выберите хотя бы один скан.");
        }
        if (files.Any(file => file.Length <= 0))
        {
            throw new InvalidDataException("Один из загруженных файлов пуст.");
        }

        var totalBytes = 0L;
        foreach (var file in files)
        {
            if (file.Length > _maxUploadBytes - totalBytes)
            {
                throw new InvalidOperationException($"Общий размер сканов превышает {_maxUploadBytes / 1024 / 1024} МБ.");
            }
            totalBytes += file.Length;
        }
        var result = new List<PreparedRegisterUpload>(files.Count);
        var normalizedBytes = 0L;
        try
        {
            foreach (var file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var prepared = await PrepareOneAsync(file, cancellationToken);
                if (prepared.Length > _maxUploadBytes - normalizedBytes)
                {
                    await prepared.DisposeAsync();
                    throw new InvalidOperationException(
                        $"После нормализации общий размер PNG превышает {_maxUploadBytes / 1024 / 1024} МБ.");
                }
                normalizedBytes += prepared.Length;
                result.Add(prepared);
            }
            return result;
        }
        catch
        {
            foreach (var upload in result)
            {
                await upload.DisposeAsync();
            }
            throw;
        }
    }

    private static async Task<PreparedRegisterUpload> PrepareOneAsync(
        IFormFile file,
        CancellationToken cancellationToken)
    {
        var extension = Path.GetExtension(file.FileName);
        if (!extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase)
            && !extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase)
            && !extension.Equals(".png", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Формат {extension} не поддерживается. Загрузите JPG или PNG.");
        }

        await using var input = file.OpenReadStream();
        using var original = Image.FromStream(input, useEmbeddedColorManagement: true, validateImageData: true);
        if ((long)original.Width * original.Height > MaxImagePixels)
        {
            throw new InvalidDataException($"Изображение {file.FileName} имеет слишком большое разрешение.");
        }
        ApplyExifOrientation(original);
        if (original.Width > original.Height)
        {
            original.RotateFlip(RotateFlipType.Rotate90FlipNone);
        }

        using var normalized = new Bitmap(original.Width, original.Height, PixelFormat.Format24bppRgb);
        using (var graphics = Graphics.FromImage(normalized))
        {
            graphics.Clear(Color.White);
            graphics.CompositingQuality = CompositingQuality.HighQuality;
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            graphics.DrawImage(original, new Rectangle(0, 0, normalized.Width, normalized.Height));
        }

        var output = new MemoryStream();
        normalized.Save(output, ImageFormat.Png);
        output.Position = 0;
        var baseName = Path.GetFileNameWithoutExtension(file.FileName);
        return new PreparedRegisterUpload($"{baseName}.png", output);
    }

    private static void ApplyExifOrientation(Image image)
    {
        if (!image.PropertyIdList.Contains(ExifOrientationId))
        {
            return;
        }

        var orientation = image.GetPropertyItem(ExifOrientationId).Value.FirstOrDefault();
        var rotateFlip = orientation switch
        {
            2 => RotateFlipType.RotateNoneFlipX,
            3 => RotateFlipType.Rotate180FlipNone,
            4 => RotateFlipType.Rotate180FlipX,
            5 => RotateFlipType.Rotate90FlipX,
            6 => RotateFlipType.Rotate90FlipNone,
            7 => RotateFlipType.Rotate270FlipX,
            8 => RotateFlipType.Rotate270FlipNone,
            _ => RotateFlipType.RotateNoneFlipNone
        };
        if (rotateFlip != RotateFlipType.RotateNoneFlipNone)
        {
            image.RotateFlip(rotateFlip);
        }
    }
}
