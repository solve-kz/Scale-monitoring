using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Scalemon.WebApp.Data;
using Scalemon.WebApp.Models;

namespace Scalemon.CSTests;

public sealed class WeightRegisterReviewServiceTests
{
    private const string OnePixelPng =
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=";

    [Fact]
    public async Task LegacyWeightRegisterReviewAppDataUsesSlaughterDateAndHundredths()
    {
        var storagePath = Path.Combine(Path.GetTempPath(), $"scalemon-review-{Guid.NewGuid():N}");
        try
        {
            var service = new JsonWeightRegisterReviewService(Options.Create(new WeightRegisterReviewOptions
            {
                StoragePath = storagePath
            }));
            await using var image = new MemoryStream(Convert.FromBase64String(OnePixelPng));
            var project = await service.CreateProjectAsync(
                new DateOnly(2026, 8, 12),
                new[] { new RegisterUploadFile("scan.png", "image/png", image, image.Length) });
            var sheet = Assert.Single(project.Sheets);
            await service.SaveCalibrationAsync(project.Id, sheet.Id, sheet.Calibration!);

            var data = Enumerable.Range(0, 40)
                .Select(row => Enumerable.Range(0, 10)
                    .Select(column => row == 0 && column == 0 ? "786" : string.Empty)
                    .ToArray())
                .ToArray();
            var legacyJson = JsonSerializer.Serialize(new
            {
                projectName = "weight_register_scans_2026-08-12",
                sheets = new[]
                {
                    new
                    {
                        id = "sheet-001",
                        name = "Лист 1",
                        source_file = "scan.png",
                        slaughter_date = "11.08.2026",
                        cutting_date = "12.08.2026",
                        table = new { headerRows = 2, dataRows = 40, totalRows = 2, columns = 10 },
                        data,
                        totals = new[] { new { row = 1, column = 1, ocrText = "786", value = 7.86m } }
                    }
                }
            });
            await using var result = new MemoryStream(Encoding.UTF8.GetBytes(legacyJson));

            await service.ImportRecognitionAsync(project.Id, result);

            var imported = await service.GetProjectAsync(project.Id);
            Assert.NotNull(imported);
            Assert.Equal(RegisterRecognitionStatus.Completed, imported.RecognitionStatus);
            Assert.Equal(new DateOnly(2026, 8, 11), Assert.Single(imported.Sheets).SlaughterDate);
            Assert.Equal(7.86m, Assert.Single(imported.Sheets[0].Cells).Value);

            await service.CorrectCellAsync(project.Id, sheet.Id, 1, 1, correctedValue: null, editedBy: "test");
            var cleared = await service.GetProjectAsync(project.Id);
            var clearedCell = Assert.Single(Assert.Single(cleared!.Sheets).Cells);
            Assert.Null(clearedCell.EffectiveValue);
            Assert.Equal(RegisterCellStatus.Empty, clearedCell.Status);
        }
        finally
        {
            if (Directory.Exists(storagePath))
            {
                Directory.Delete(storagePath, recursive: true);
            }
        }
    }

    [Fact]
    public async Task FailedNewProjectDoesNotLeaveOrphanDirectory()
    {
        var storagePath = Path.Combine(Path.GetTempPath(), $"scalemon-review-{Guid.NewGuid():N}");
        try
        {
            var service = new JsonWeightRegisterReviewService(Options.Create(new WeightRegisterReviewOptions
            {
                StoragePath = storagePath
            }));
            await using var image = new MemoryStream(Convert.FromBase64String(OnePixelPng));
            await using var invalid = new MemoryStream(new byte[] { 1, 2, 3 });

            await Assert.ThrowsAsync<InvalidOperationException>(() => service.CreateProjectAsync(
                new DateOnly(2026, 8, 12),
                new[]
                {
                    new RegisterUploadFile("scan.png", "image/png", image, image.Length),
                    new RegisterUploadFile("not-an-image.txt", "text/plain", invalid, invalid.Length)
                }));

            Assert.Empty(Directory.EnumerateDirectories(storagePath));
        }
        finally
        {
            if (Directory.Exists(storagePath))
            {
                Directory.Delete(storagePath, recursive: true);
            }
        }
    }
}
