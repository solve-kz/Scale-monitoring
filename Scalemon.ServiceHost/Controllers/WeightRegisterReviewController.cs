using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.IO.Compression;
using System.Text.Json;
using Scalemon.ServiceHost.Services;
using Scalemon.WebApp.Data;
using Scalemon.WebApp.Models;

namespace Scalemon.ServiceHost.Controllers;

/// <summary>Принимает сканы и совместимый JSON результата распознавания.</summary>
[ApiController]
[Authorize]
[Route("api/weight-register")]
public sealed class WeightRegisterReviewController : ControllerBase
{
    private const long MaxRecognitionResultBytes = 5L * 1024 * 1024;
    private readonly IWeightRegisterReviewService _reviewService;
    private readonly IRegisterImageUploadPreprocessor _uploadPreprocessor;

    public WeightRegisterReviewController(
        IWeightRegisterReviewService reviewService,
        IRegisterImageUploadPreprocessor uploadPreprocessor)
    {
        _reviewService = reviewService;
        _uploadPreprocessor = uploadPreprocessor;
    }

    /// <summary>Создаёт проект ручного реестра из нескольких изображений.</summary>
    [HttpPost("uploads")]
    [Authorize(Roles = "Editor,Admin")]
    public async Task<IActionResult> UploadAsync(
        [FromQuery] DateOnly cuttingDate,
        [FromForm] List<IFormFile> files,
        CancellationToken cancellationToken)
    {
        if (files.Count == 0)
        {
            return BadRequest(new { error = "Выберите хотя бы один скан." });
        }

        IReadOnlyList<PreparedRegisterUpload> prepared = Array.Empty<PreparedRegisterUpload>();
        try
        {
            prepared = await _uploadPreprocessor.PrepareAsync(files, cancellationToken);
            var uploads = prepared
                .Select(file => new RegisterUploadFile(
                    file.FileName,
                    "image/png",
                    file.Content,
                    file.Length))
                .ToArray();
            var project = await _reviewService.CreateProjectAsync(cuttingDate, uploads, cancellationToken);
            return Ok(new { projectId = project.Id, projectName = project.ProjectName });
        }
        catch (Exception ex) when (ex is InvalidOperationException or InvalidDataException or ArgumentException)
        {
            return BadRequest(new { error = ex.Message });
        }
        finally
        {
            foreach (var upload in prepared)
            {
                await upload.DisposeAsync();
            }
        }
    }

    /// <summary>Добавляет сканы в существующий проект текущего дня.</summary>
    [HttpPost("projects/{projectId}/uploads")]
    [Authorize(Roles = "Editor,Admin")]
    public async Task<IActionResult> AddSheetsAsync(
        string projectId,
        [FromForm] List<IFormFile> files,
        CancellationToken cancellationToken)
    {
        if (files.Count == 0)
        {
            return BadRequest(new { error = "Выберите хотя бы один скан." });
        }

        IReadOnlyList<PreparedRegisterUpload> prepared = Array.Empty<PreparedRegisterUpload>();
        try
        {
            prepared = await _uploadPreprocessor.PrepareAsync(files, cancellationToken);
            var uploads = prepared
                .Select(file => new RegisterUploadFile(file.FileName, "image/png", file.Content, file.Length))
                .ToArray();
            var project = await _reviewService.AddSheetsAsync(projectId, uploads, cancellationToken);
            return Ok(new { projectId = project.Id, projectName = project.ProjectName });
        }
        catch (Exception ex) when (ex is InvalidOperationException or InvalidDataException or ArgumentException or KeyNotFoundException)
        {
            return BadRequest(new { error = ex.Message });
        }
        finally
        {
            foreach (var upload in prepared)
            {
                await upload.DisposeAsync();
            }
        }
    }

    /// <summary>Импортирует современный или совместимый с WeightRegisterReviewApp JSON.</summary>
    [HttpPost("projects/{projectId}/recognition-result")]
    [Authorize(Roles = "Editor,Admin")]
    public async Task<IActionResult> ImportRecognitionAsync(
        string projectId,
        [FromForm] IFormFile file,
        CancellationToken cancellationToken)
    {
        try
        {
            if (file.Length <= 0 || file.Length > MaxRecognitionResultBytes)
            {
                return BadRequest(new { error = "JSON результата должен иметь размер от 1 байта до 5 МБ." });
            }

            await using var stream = file.OpenReadStream();
            await _reviewService.ImportRecognitionAsync(projectId, stream, cancellationToken);
            return Ok(new { projectId });
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException or ArgumentException)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>Выдаёт изображение листа без раскрытия пути локального хранилища.</summary>
    [HttpGet("projects/{projectId}/sheets/{sheetId}/image")]
    public async Task<IActionResult> ImageAsync(
        string projectId,
        string sheetId,
        CancellationToken cancellationToken)
    {
        var image = await _reviewService.OpenImageAsync(projectId, sheetId, cancellationToken);
        return image is null
            ? NotFound()
            : File(image.Content, image.ContentType, enableRangeProcessing: true);
    }

    /// <summary>Скачивает задание и подготовленные сканы одним пакетом для агента распознавания.</summary>
    [HttpGet("projects/{projectId}/recognition-request")]
    [Authorize(Roles = "Editor,Admin")]
    public async Task<IActionResult> DownloadRecognitionRequestAsync(
        string projectId,
        CancellationToken cancellationToken)
    {
        MemoryStream? package = null;
        try
        {
            var path = _reviewService.GetRecognitionRequestPath(projectId);
            var project = await _reviewService.GetProjectAsync(projectId, cancellationToken);
            if (!System.IO.File.Exists(path) || project is null)
            {
                return NotFound();
            }

            package = new MemoryStream();
            using (var archive = new ZipArchive(package, ZipArchiveMode.Create, leaveOpen: true))
            {
                var requestEntry = archive.CreateEntry("recognition-request.json", CompressionLevel.Optimal);
                await using (var requestOutput = requestEntry.Open())
                await using (var requestInput = System.IO.File.OpenRead(path))
                {
                    await requestInput.CopyToAsync(requestOutput, cancellationToken);
                }

                foreach (var sheet in project.Sheets.Where(sheet => !sheet.IsRecognitionComplete))
                {
                    var image = await _reviewService.OpenImageAsync(projectId, sheet.Id, cancellationToken);
                    if (image is null)
                    {
                        throw new KeyNotFoundException($"Изображение листа {sheet.Name} не найдено.");
                    }

                    await using var imageInput = image.Content;
                    var imageEntry = archive.CreateEntry(
                        $"source/{Path.GetFileName(sheet.SourceFile)}",
                        CompressionLevel.NoCompression);
                    await using var imageOutput = imageEntry.Open();
                    await imageInput.CopyToAsync(imageOutput, cancellationToken);
                }
            }

            package.Position = 0;
            return File(package, "application/zip", $"weight-register-{project.CuttingDate:yyyy-MM-dd}.zip");
        }
        catch (Exception ex) when (ex is ArgumentException or KeyNotFoundException)
        {
            package?.Dispose();
            return NotFound();
        }
        catch
        {
            package?.Dispose();
            throw;
        }
    }
}
