using Scalemon.Common.Updates;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using Scalemon.WebApp.Data;
using Scalemon.WebApp.Models;

namespace Scalemon.ServiceHost.Services;

/// <summary>Распознаёт один проект ручного реестра во внешнем визуальном API.</summary>
public interface IWeightRegisterRecognizer
{
    /// <summary>Возвращает проект с распознанными ячейками и итогами.</summary>
    Task<WeightRegisterProject> RecognizeAsync(
        WeightRegisterProject project,
        CancellationToken cancellationToken = default);
}

/// <summary>Обрабатывает файловую очередь распознавания без блокировки службы весов.</summary>
public sealed class WeightRegisterRecognitionWorker : BackgroundService
{
    private readonly IWeightRegisterReviewService _reviewService;
    private readonly IWeightRegisterRecognizer _recognizer;
    private readonly ILogger<WeightRegisterRecognitionWorker> _logger;
    private readonly WeightRegisterRecognitionOptions _options;
    private readonly WeightRegisterRecognitionCancellationRegistry _cancellationRegistry;

    public WeightRegisterRecognitionWorker(
        IWeightRegisterReviewService reviewService,
        IWeightRegisterRecognizer recognizer,
        IOptions<WeightRegisterReviewOptions> options,
        WeightRegisterRecognitionCancellationRegistry cancellationRegistry,
        ILogger<WeightRegisterRecognitionWorker> logger)
    {
        _reviewService = reviewService;
        _recognizer = recognizer;
        _logger = logger;
        _options = options.Value.Recognition;
        _cancellationRegistry = cancellationRegistry;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            _reviewService.ReportRecognitionWorkerHeartbeat("Автоматическое распознавание отключено в настройках.");
            return;
        }

        var pollInterval = TimeSpan.FromSeconds(Math.Clamp(_options.PollIntervalSeconds, 2, 60));
        _reviewService.ReportRecognitionWorkerHeartbeat("Исполнитель распознавания запущен.");
        while (!stoppingToken.IsCancellationRequested)
        {
            if (MaintenanceGate.Shared.IsClosed) { await Task.Delay(pollInterval, stoppingToken); continue; }
            IDisposable? maintenanceOperation = null;
            WeightRegisterProject? project = null;
            CancellationTokenSource? jobCancellation = null;
            try
            {
                maintenanceOperation = MaintenanceGate.Shared.Enter();
                _reviewService.ReportRecognitionWorkerHeartbeat("Проверка очереди распознавания.");
                project = await _reviewService.TryClaimRecognitionAsync(stoppingToken);
                if (project is null)
                {
                    await Task.Delay(pollInterval, stoppingToken);
                    continue;
                }

                _reviewService.ReportRecognitionWorkerHeartbeat($"Выполняется проект «{project.ProjectName}».");
                jobCancellation = _cancellationRegistry.Register(project.Id, stoppingToken);
                var recognized = await _recognizer.RecognizeAsync(project, jobCancellation.Token);
                var completed = await _reviewService.CompleteRecognitionAsync(project.Id, recognized, stoppingToken);
                _reviewService.ReportRecognitionWorkerHeartbeat(completed
                    ? "Последнее задание успешно завершено."
                    : "Последнее задание отменено.");
                if (!completed)
                {
                    continue;
                }
                _logger.LogInformation(
                    "Распознавание ручного реестра {ProjectId} завершено: {SheetCount} листов",
                    project.Id,
                    project.Sheets.Count);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (OperationCanceledException) when (project is not null && jobCancellation?.IsCancellationRequested == true)
            {
                await _reviewService.FinishRecognitionCancellationAsync(project.Id, stoppingToken);
                _reviewService.ReportRecognitionWorkerHeartbeat("Последнее задание отменено.");
                _logger.LogInformation("Распознавание ручного реестра {ProjectId} отменено", project.Id);
            }
            catch (Exception ex)
            {
                if (project is not null)
                {
                    try
                    {
                        await _reviewService.FailRecognitionAsync(
                            project.Id,
                            ToUserMessage(ex),
                            stoppingToken);
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                    {
                        break;
                    }
                }

                _logger.LogError(
                    ex,
                    "Не удалось распознать ручной реестр {ProjectId}",
                    project?.Id ?? "не выбран");
                _reviewService.ReportRecognitionWorkerHeartbeat("Последнее задание завершилось с ошибкой.");

                try
                {
                    await Task.Delay(pollInterval, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
            }
            finally
            {
                maintenanceOperation?.Dispose();
                if (project is not null && jobCancellation is not null)
                {
                    _cancellationRegistry.Unregister(project.Id, jobCancellation);
                }
            }
        }
    }

    private static string ToUserMessage(Exception exception)
        => exception switch
        {
            WeightRegisterRecognitionException => exception.Message,
            InvalidOperationException => exception.Message,
            HttpRequestException => "Сервис распознавания недоступен. Проверьте сеть и настройки API.",
            TaskCanceledException => "Истёк тайм-аут распознавания листа.",
            _ => "Распознавание завершилось с ошибкой. Подробности записаны в журнал службы."
        };
}

/// <summary>Исполнитель визуального распознавания через OpenAI Responses API.</summary>
public sealed class OpenAiWeightRegisterRecognizer : IWeightRegisterRecognizer
{
    private static readonly JsonSerializerOptions ApiJsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    private const string Instructions = """
        Ты визуально переписываешь русский рукописный реестр убойного веса. Работай только по изображениям,
        не подгоняй значения под итоги и не сравнивай их с автоматическим реестром. Изображение уже физически
        выровнено. Первое изображение содержит верх листа: прочитай в нём строки «дата забоя» и «дата разделки».
        Второе изображение показывает таблицу целиком и предназначено для проверки геометрии и отсутствия сдвига.
        После него переданы увеличенные блоки строк с подписями диапазонов; значения тела переписывай именно по
        этим блокам. В каждом блоке сначала сверь номер первой и последней строки, чтобы исключить сдвиг на строку
        или колонку. Последнее изображение отдельно показывает рукописные итоги. Первая колонка таблицы содержит
        номера строк, затем идут 10 колонок данных. Верни сырой рукописный текст без добавления десятичной точки.
        Тело таблицы имеет 40 строк.
        Заполняй координаты в порядке колонок: сначала строки 1..40 колонки 1, затем колонка 2 и так далее.
        Возвращай только заполненные или действительно сомнительные ячейки; пустые в конце листа не возвращай.
        Для неразборчивой цифры сохрани наиболее вероятный текст, поставь Suspect и напиши конкретную причину.
        Не исправляй цифры ради совпадения суммы. Отдельно прочитай рукописные итоги в нижних строках.
        Перед ответом перепроверь отсутствие сдвига строк/колонок. В verifiedAnchors верни ровно четыре проверки
        позиций 1:1, 1:10, 40:1, 40:10: filled=true и тот же ocrText для заполненной позиции либо filled=false
        и ocrText=null для действительно пустой. Значение filledCells должно точно равняться числу фактически
        заполненных ячеек тела.
        """;

    private readonly IWeightRegisterReviewService _reviewService;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly WeightRegisterRecognitionOptions _options;

    public OpenAiWeightRegisterRecognizer(
        IWeightRegisterReviewService reviewService,
        IHttpClientFactory httpClientFactory,
        IOptions<WeightRegisterReviewOptions> options)
    {
        _reviewService = reviewService;
        _httpClientFactory = httpClientFactory;
        _options = options.Value.Recognition;
    }

    /// <inheritdoc />
    public async Task<WeightRegisterProject> RecognizeAsync(
        WeightRegisterProject project,
        CancellationToken cancellationToken = default)
    {
        ValidateConfiguration();
        var targetSheets = project.Sheets.Where(sheet => !sheet.IsRecognitionComplete).ToArray();
        if (targetSheets.Length == 0)
        {
            throw new WeightRegisterRecognitionException("В проекте нет новых листов для распознавания.");
        }
        for (var index = 0; index < targetSheets.Length; index++)
        {
            var sheet = targetSheets[index];
            cancellationToken.ThrowIfCancellationRequested();
            await _reviewService.UpdateRecognitionProgressAsync(
                project.Id,
                index,
                targetSheets.Length,
                sheet.Name,
                cancellationToken);
            if (!sheet.IsCalibrationConfirmed || sheet.Calibration is null)
            {
                throw new WeightRegisterRecognitionException($"Лист «{sheet.Name}» не откалиброван.");
            }

            var image = await _reviewService.OpenImageAsync(project.Id, sheet.Id, cancellationToken)
                ?? throw new WeightRegisterRecognitionException($"Исходное изображение листа «{sheet.Name}» не найдено.");
            await using var source = image.Content;
            using var prepared = PrepareImages(source, sheet.Calibration, sheet.Table);
            var payload = await RecognizeSheetAsync(project, sheet, prepared, cancellationToken);
            ApplyPayload(project, sheet, payload);
            sheet.IsRecognitionComplete = true;
            await _reviewService.UpdateRecognitionProgressAsync(
                project.Id,
                index + 1,
                targetSheets.Length,
                index + 1 < targetSheets.Length ? targetSheets[index + 1].Name : null,
                cancellationToken);
        }

        return project;
    }

    private async Task<RecognizedSheetPayload> RecognizeSheetAsync(
        WeightRegisterProject project,
        WeightRegisterSheet sheet,
        PreparedScanImages preparedImages,
        CancellationToken cancellationToken)
    {
        var dateHeaderData = Convert.ToBase64String(preparedImages.DateHeader.ToArray());
        var tableData = Convert.ToBase64String(preparedImages.TableOverview.ToArray());
        var prompt = $"Лист: {sheet.Name}. Ожидаемая дата разделки проекта: {project.CuttingDate:dd.MM.yyyy}. "
            + "Найди строку «дата разделки» на первом увеличенном изображении и внимательно проверь каждую цифру. "
            + "Ожидаемая дата служит подсказкой, но не подменяет изображение: при неразборчивой дате верни null. "
            + $"Таблица: {sheet.Table.DataRows} строк данных, {sheet.Table.Columns} колонок данных, "
            + $"{sheet.Table.TotalRows} нижних строк итогов.";
        var content = new List<object>
        {
            new { type = "input_text", text = prompt },
            new { type = "input_text", text = "Верх листа с датами забоя и разделки:" },
            new
            {
                type = "input_image",
                image_url = $"data:image/png;base64,{dateHeaderData}",
                detail = "original"
            },
            new { type = "input_text", text = "Обзор полной таблицы для проверки сетки и координат:" },
            new
            {
                type = "input_image",
                image_url = $"data:image/png;base64,{tableData}",
                detail = "original"
            }
        };
        foreach (var block in preparedImages.ReviewBlocks)
        {
            content.Add(new
            {
                type = "input_text",
                text = $"Увеличенный блок строк {block.FirstRow}–{block.LastRow}; перепиши все 10 колонок этих строк:"
            });
            content.Add(new
            {
                type = "input_image",
                image_url = $"data:image/png;base64,{Convert.ToBase64String(block.Image.ToArray())}",
                detail = "original"
            });
        }
        content.Add(new { type = "input_text", text = "Отдельный увеличенный блок рукописных итогов:" });
        content.Add(new
        {
            type = "input_image",
            image_url = $"data:image/png;base64,{Convert.ToBase64String(preparedImages.Totals.ToArray())}",
            detail = "original"
        });
        var requestBody = new
        {
            model = _options.Model,
            store = false,
            instructions = Instructions,
            input = new object[]
            {
                new
                {
                    role = "user",
                    content
                }
            },
            text = new
            {
                format = new
                {
                    type = "json_schema",
                    name = "weight_register_sheet",
                    strict = true,
                    schema = CreateResponseSchema(sheet.Table)
                }
            },
            max_output_tokens = Math.Clamp(_options.MaxOutputTokens, 1000, 100000)
        };

        var requestBytes = JsonSerializer.SerializeToUtf8Bytes(requestBody, ApiJsonOptions);
        var requestSizeMb = requestBytes.Length / 1024d / 1024d;
        var clientRequestId = Guid.NewGuid().ToString("D");
        using var request = new HttpRequestMessage(HttpMethod.Post, _options.Endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", GetApiKey());
        request.Headers.TryAddWithoutValidation("X-Client-Request-Id", clientRequestId);
        request.Content = new ByteArrayContent(requestBytes);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var timeoutSeconds = Math.Clamp(_options.RequestTimeoutSeconds, 30, 900);
        timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        var client = _httpClientFactory.CreateClient(nameof(OpenAiWeightRegisterRecognizer));
        _reviewService.ReportRecognitionWorkerHeartbeat(
            $"Отправляется лист «{sheet.Name}»: {requestSizeMb:0.0} МБ, запрос {clientRequestId}.");
        var requestTimer = Stopwatch.StartNew();
        HttpResponseMessage response;
        try
        {
            response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new WeightRegisterRecognitionException(
                $"OpenAI не ответил за {timeoutSeconds} сек. Размер запроса: {requestSizeMb:0.0} МБ; "
                + $"X-Client-Request-Id: {clientRequestId}.",
                ex);
        }
        catch (HttpRequestException ex)
        {
            throw new WeightRegisterRecognitionException(
                $"Не удалось подключиться к OpenAI: {ex.Message} X-Client-Request-Id: {clientRequestId}.",
                ex);
        }

        using (response)
        {
            var responseBody = await response.Content.ReadAsStringAsync(timeout.Token);
            var serverRequestId = response.Headers.TryGetValues("x-request-id", out var requestIds)
                ? requestIds.FirstOrDefault()
                : null;
            _reviewService.ReportRecognitionWorkerHeartbeat(
                $"OpenAI ответил за {requestTimer.Elapsed.TotalSeconds:0} сек.; "
                + $"запрос {serverRequestId ?? clientRequestId}.");
            if (!response.IsSuccessStatusCode)
            {
                throw new WeightRegisterRecognitionException(
                    $"Сервис распознавания вернул HTTP {(int)response.StatusCode}: {ExtractApiError(responseBody)} "
                    + $"Запрос: {serverRequestId ?? clientRequestId}.");
            }

            var outputText = ExtractOutputText(responseBody);
            try
            {
                return JsonSerializer.Deserialize<RecognizedSheetPayload>(outputText, ApiJsonOptions)
                    ?? throw new WeightRegisterRecognitionException("Сервис распознавания вернул пустой JSON.");
            }
            catch (JsonException ex)
            {
                throw new WeightRegisterRecognitionException("Структурированный ответ распознавания повреждён.", ex);
            }
        }
    }

    private void ValidateConfiguration()
    {
        if (string.IsNullOrWhiteSpace(_options.Model))
        {
            throw new WeightRegisterRecognitionException("В настройках не указана модель AI-распознавания.");
        }
        if (string.IsNullOrWhiteSpace(_options.ApiKeyEnvironmentVariable))
        {
            throw new WeightRegisterRecognitionException("Не указано имя переменной окружения с API-ключом.");
        }
        if (!Uri.TryCreate(_options.Endpoint, UriKind.Absolute, out var endpoint)
            || endpoint.Scheme != Uri.UriSchemeHttps
                && !(endpoint.Scheme == Uri.UriSchemeHttp && endpoint.IsLoopback))
        {
            throw new WeightRegisterRecognitionException("Endpoint распознавания должен использовать HTTPS (HTTP разрешён только для localhost).");
        }
    }

    private string GetApiKey()
    {
        var apiKey = Environment.GetEnvironmentVariable(_options.ApiKeyEnvironmentVariable);
        return string.IsNullOrWhiteSpace(apiKey)
            ? throw new WeightRegisterRecognitionException(
                $"Переменная окружения {_options.ApiKeyEnvironmentVariable} не задана для учётной записи Windows-службы.")
            : apiKey;
    }

    private static PreparedScanImages PrepareImages(
        Stream source,
        RegisterTableCalibration calibration,
        RegisterTableShape tableShape)
    {
        if (tableShape.HeaderRows < 0 || tableShape.DataRows <= 0 || tableShape.TotalRows <= 0)
        {
            throw new WeightRegisterRecognitionException("Размерность таблицы для распознавания задана неверно.");
        }

        using var original = Image.FromStream(source, useEmbeddedColorManagement: true, validateImageData: true);
        var left = Math.Clamp((int)Math.Floor(calibration.TableLeft), 0, original.Width - 1);
        var top = Math.Clamp((int)Math.Floor(calibration.TableTop), 0, original.Height - 1);
        var right = Math.Clamp((int)Math.Ceiling(calibration.TableRight), left + 1, original.Width);
        var bottom = Math.Clamp((int)Math.Ceiling(calibration.TableBottom), top + 1, original.Height);

        using var rotated = new Bitmap(original.Width, original.Height, PixelFormat.Format24bppRgb);
        using (var graphics = Graphics.FromImage(rotated))
        {
            graphics.Clear(Color.White);
            graphics.CompositingQuality = CompositingQuality.HighQuality;
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            graphics.SmoothingMode = SmoothingMode.HighQuality;
            var centerX = original.Width / 2f;
            var centerY = original.Height / 2f;
            graphics.TranslateTransform(centerX, centerY);
            graphics.RotateTransform((float)calibration.RotationDegrees);
            graphics.TranslateTransform(-centerX, -centerY);
            graphics.DrawImageUnscaled(original, 0, 0);
        }

        var headerBottom = Math.Clamp(
            Math.Max((int)Math.Ceiling(calibration.TableTop), (int)Math.Ceiling(original.Height * 0.12d)),
            1,
            Math.Max(1, (int)Math.Ceiling(original.Height * 0.30d)));
        var table = new Rectangle(left, top, right - left, bottom - top);
        var gridRows = calibration.TableTop < calibration.TableBottom
            ? (double)(tableShape.HeaderRows + tableShape.DataRows + tableShape.TotalRows)
            : throw new WeightRegisterRecognitionException("Границы таблицы для распознавания заданы неверно.");
        var rowHeight = table.Height / gridRows;
        var bodyTop = table.Top + rowHeight * tableShape.HeaderRows;
        const int rowsPerBlock = 8;
        var reviewBlocks = new List<PreparedReviewBlock>();
        MemoryStream? dateHeader = null;
        MemoryStream? tableOverview = null;
        MemoryStream? totals = null;
        try
        {
            for (var firstRow = 1; firstRow <= tableShape.DataRows; firstRow += rowsPerBlock)
            {
                var lastRow = Math.Min(tableShape.DataRows, firstRow + rowsPerBlock - 1);
                var blockTop = Math.Clamp(
                    (int)Math.Floor(bodyTop + (firstRow - 1) * rowHeight),
                    table.Top,
                    table.Bottom - 1);
                var blockBottom = Math.Clamp(
                    (int)Math.Ceiling(bodyTop + lastRow * rowHeight),
                    blockTop + 1,
                    table.Bottom);
                reviewBlocks.Add(new PreparedReviewBlock(
                    firstRow,
                    lastRow,
                    CropToPng(rotated, new Rectangle(table.Left, blockTop, table.Width, blockBottom - blockTop), 2d)));
            }

            var totalsTop = Math.Clamp(
                (int)Math.Floor(bodyTop + tableShape.DataRows * rowHeight),
                table.Top,
                table.Bottom - 1);
            var dateHeaderLeft = Math.Clamp((int)Math.Floor(original.Width * 0.42d), 0, original.Width - 1);
            dateHeader = CropToPng(
                rotated,
                new Rectangle(dateHeaderLeft, 0, original.Width - dateHeaderLeft, headerBottom),
                2d);
            tableOverview = CropToPng(rotated, table);
            totals = CropToPng(
                rotated,
                new Rectangle(table.Left, totalsTop, table.Width, table.Bottom - totalsTop),
                2d);
            return new PreparedScanImages(dateHeader, tableOverview, reviewBlocks, totals);
        }
        catch
        {
            dateHeader?.Dispose();
            tableOverview?.Dispose();
            foreach (var block in reviewBlocks)
            {
                block.Image.Dispose();
            }
            totals?.Dispose();
            throw;
        }
    }

    private static MemoryStream CropToPng(Bitmap source, Rectangle bounds, double scale = 1d)
    {
        const double maxOutputWidth = 2400d;
        const double maxOutputHeight = 3000d;
        var effectiveScale = Math.Min(
            scale,
            Math.Min(maxOutputWidth / bounds.Width, maxOutputHeight / bounds.Height));
        effectiveScale = Math.Max(0.05d, effectiveScale);
        var outputWidth = Math.Max(1, (int)Math.Ceiling(bounds.Width * effectiveScale));
        var outputHeight = Math.Max(1, (int)Math.Ceiling(bounds.Height * effectiveScale));
        using var cropped = new Bitmap(outputWidth, outputHeight, PixelFormat.Format24bppRgb);
        using (var graphics = Graphics.FromImage(cropped))
        {
            graphics.Clear(Color.White);
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            graphics.DrawImage(
                source,
                new Rectangle(0, 0, cropped.Width, cropped.Height),
                bounds,
                GraphicsUnit.Pixel);
        }

        var result = new MemoryStream();
        cropped.Save(result, ImageFormat.Png);
        result.Position = 0;
        return result;
    }

    private static void ApplyPayload(
        WeightRegisterProject project,
        WeightRegisterSheet sheet,
        RecognizedSheetPayload payload)
    {
        if (payload.Cells is null || payload.Totals is null || payload.VerifiedAnchors is null)
        {
            throw new WeightRegisterRecognitionException(
                $"Лист «{sheet.Name}»: структурированный ответ не содержит обязательные массивы.");
        }

        sheet.RecognitionWarnings = (payload.QualityNotes ?? new List<string>())
            .Where(note => !string.IsNullOrWhiteSpace(note))
            .Select(note => note.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        AddWarning(sheet, ValidateCuttingDate(project, sheet, payload.CuttingDate));
        AddWarning(sheet, ValidateSlaughterDate(project, sheet, payload.SlaughterDate));
        var duplicate = payload.Cells
            .GroupBy(cell => (cell.Row, cell.Column))
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
        {
            throw new WeightRegisterRecognitionException(
                $"Лист «{sheet.Name}»: ячейка {duplicate.Key.Row}:{duplicate.Key.Column} распознана дважды.");
        }

        var recognizedCells = new List<RegisterReviewCell>();
        foreach (var candidate in payload.Cells)
        {
            if (candidate.Row < 1 || candidate.Row > sheet.Table.DataRows
                || candidate.Column < 1 || candidate.Column > sheet.Table.Columns)
            {
                throw new WeightRegisterRecognitionException(
                    $"Лист «{sheet.Name}»: координаты {candidate.Row}:{candidate.Column} находятся вне таблицы.");
            }
            if (candidate.Status == RecognizedCellStatus.Empty && string.IsNullOrWhiteSpace(candidate.OcrText))
            {
                continue;
            }

            var normalization = WeightRegisterOcrNormalizer.NormalizeBody(candidate.OcrText);
            var comment = JoinComments(
                candidate.Comment,
                normalization.Reason,
                candidate.Status == RecognizedCellStatus.Empty
                    ? "модель одновременно пометила непустой текст как Empty"
                    : null);
            var status = candidate.Status == RecognizedCellStatus.Suspect
                || candidate.Status == RecognizedCellStatus.Empty
                || normalization.IsSuspect
                || !normalization.Value.HasValue
                    ? RegisterCellStatus.Suspect
                    : RegisterCellStatus.Unreviewed;
            recognizedCells.Add(new RegisterReviewCell
            {
                Row = candidate.Row,
                Column = candidate.Column,
                OcrText = normalization.RawText,
                Value = normalization.Value,
                Status = status,
                Comment = comment,
                Confidence = candidate.Confidence is null ? null : Math.Clamp(candidate.Confidence.Value, 0d, 1d)
            });
        }

        ValidateQuality(sheet, payload, recognizedCells);
        sheet.Cells = recognizedCells
            .OrderBy(cell => cell.Column)
            .ThenBy(cell => cell.Row)
            .ToList();
        var duplicateTotal = payload.Totals
            .GroupBy(total => (total.Row, total.Column))
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicateTotal is not null)
        {
            throw new WeightRegisterRecognitionException(
                $"Лист «{sheet.Name}»: итог {duplicateTotal.Key.Row}:{duplicateTotal.Key.Column} распознан дважды.");
        }

        sheet.Totals = payload.Totals
            .Where(candidate => candidate.Status != RecognizedCellStatus.Empty || !string.IsNullOrWhiteSpace(candidate.OcrText))
            .Select(candidate =>
        {
            if (candidate.Row < 1 || candidate.Row > sheet.Table.TotalRows
                || candidate.Column < 1 || candidate.Column > sheet.Table.Columns)
            {
                throw new WeightRegisterRecognitionException(
                    $"Лист «{sheet.Name}»: координаты итога {candidate.Row}:{candidate.Column} находятся вне таблицы.");
            }

            var normalization = WeightRegisterOcrNormalizer.NormalizeTotal(candidate.OcrText);
            return new RegisterReviewTotal
            {
                Row = candidate.Row,
                Column = candidate.Column,
                OcrText = normalization.RawText,
                Value = normalization.Value,
                Status = candidate.Status == RecognizedCellStatus.Suspect
                    || normalization.IsSuspect
                    || !normalization.Value.HasValue
                        ? RegisterCellStatus.Suspect
                        : RegisterCellStatus.Unreviewed,
                Comment = JoinComments(candidate.Comment, normalization.Reason)
            };
        }).ToList();
        ValidateTotals(sheet);
    }

    private static string? ValidateCuttingDate(
        WeightRegisterProject project,
        WeightRegisterSheet sheet,
        string? recognizedDate)
    {
        var formats = new[] { "dd.MM.yyyy", "d.M.yyyy", "yyyy-MM-dd", "dd-MM-yyyy" };
        if (!DateOnly.TryParseExact(
                recognizedDate,
                formats,
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var date))
        {
            sheet.CuttingDate = project.CuttingDate;
            return $"Лист «{sheet.Name}»: модель не смогла уверенно прочитать дату разделки; используется дата проекта {project.CuttingDate:dd.MM.yyyy}.";
        }
        if (date != project.CuttingDate)
        {
            sheet.CuttingDate = project.CuttingDate;
            return $"Лист «{sheet.Name}»: модель прочитала дату разделки как {date:dd.MM.yyyy}; используется подтверждённая дата проекта {project.CuttingDate:dd.MM.yyyy}. Проверьте заголовок скана.";
        }

        sheet.CuttingDate = date;
        return null;
    }

    private static string? ValidateSlaughterDate(
        WeightRegisterProject project,
        WeightRegisterSheet sheet,
        string? recognizedDate)
    {
        var formats = new[] { "dd.MM.yyyy", "d.M.yyyy", "yyyy-MM-dd", "dd-MM-yyyy" };
        if (!DateOnly.TryParseExact(
                recognizedDate,
                formats,
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var date))
        {
            sheet.SlaughterDate = null;
            return $"Лист «{sheet.Name}»: модель не смогла уверенно прочитать дату забоя. Проверьте заголовок скана.";
        }
        if (date > project.CuttingDate)
        {
            sheet.SlaughterDate = null;
            return $"Лист «{sheet.Name}»: модель прочитала дату забоя как {date:dd.MM.yyyy}, что позже даты разделки. Значение не принято.";
        }

        sheet.SlaughterDate = date;
        return null;
    }

    private static void AddWarning(WeightRegisterSheet sheet, string? warning)
    {
        if (!string.IsNullOrWhiteSpace(warning)
            && !sheet.RecognitionWarnings.Contains(warning, StringComparer.OrdinalIgnoreCase))
        {
            sheet.RecognitionWarnings.Add(warning);
        }
    }

    private static void ValidateQuality(
        WeightRegisterSheet sheet,
        RecognizedSheetPayload payload,
        IReadOnlyList<RegisterReviewCell> cells)
    {
        var filled = cells.Where(cell => cell.Value.HasValue).ToArray();
        if (filled.Length == 0)
        {
            throw new WeightRegisterRecognitionException($"Лист «{sheet.Name}»: не найдено ни одной заполненной ячейки.");
        }
        if (payload.FilledCells != cells.Count)
        {
            throw new WeightRegisterRecognitionException(
                $"Лист «{sheet.Name}»: контрольное число заполненных ячеек ({payload.FilledCells}) не совпало с результатом ({cells.Count}).");
        }

        ValidateAnchors(sheet, payload, cells);

        var occupiedColumns = cells.GroupBy(cell => cell.Column).ToArray();
        var expectedPositions = occupiedColumns.Sum(group => group.Max(cell => cell.Row));
        var coveredPositions = occupiedColumns.Sum(group => group
            .Where(cell => cell.Status != RegisterCellStatus.Empty)
            .Select(cell => cell.Row)
            .Distinct()
            .Count());
        var unexpectedEmptyPercent = (expectedPositions - coveredPositions) * 100d / expectedPositions;
        if (unexpectedEmptyPercent > 2d)
        {
            throw new WeightRegisterRecognitionException(
                $"Лист «{sheet.Name}»: {unexpectedEmptyPercent:0.0}% неожиданных пустых позиций до последней записи; возможен сдвиг таблицы.");
        }

        var suspects = cells.Where(cell => cell.Status == RegisterCellStatus.Suspect).ToArray();
        var suspectPercent = suspects.Length * 100d / cells.Count;
        if (suspects.Any(cell => string.IsNullOrWhiteSpace(cell.Comment) || cell.Comment.Length < 12))
        {
            throw new WeightRegisterRecognitionException(
                $"Лист «{sheet.Name}»: среди {suspectPercent:0.0}% сомнительных ячеек есть позиции без конкретного визуального пояснения.");
        }
    }

    private static void ValidateAnchors(
        WeightRegisterSheet sheet,
        RecognizedSheetPayload payload,
        IReadOnlyList<RegisterReviewCell> cells)
    {
        var expected = new HashSet<(int Row, int Column)>
        {
            (1, 1),
            (1, sheet.Table.Columns),
            (sheet.Table.DataRows, 1),
            (sheet.Table.DataRows, sheet.Table.Columns)
        };
        var anchors = payload.VerifiedAnchors
            .GroupBy(anchor => (anchor.Row, anchor.Column))
            .ToDictionary(group => group.Key, group => group.ToArray());
        if (anchors.Count != expected.Count
            || anchors.Any(pair => pair.Value.Length != 1)
            || !expected.SetEquals(anchors.Keys))
        {
            throw new WeightRegisterRecognitionException(
                $"Лист «{sheet.Name}»: не подтверждены четыре угловые координаты таблицы.");
        }

        foreach (var position in expected)
        {
            var anchor = anchors[position][0];
            var cell = cells.SingleOrDefault(item => item.Row == position.Row && item.Column == position.Column);
            if (anchor.Filled != (cell is not null)
                || anchor.Filled
                    && !string.Equals(anchor.OcrText?.Trim(), cell?.OcrText?.Trim(), StringComparison.Ordinal))
            {
                throw new WeightRegisterRecognitionException(
                    $"Лист «{sheet.Name}»: угловая проверка {position.Row}:{position.Column} не совпала с основной расшифровкой.");
            }
        }
    }

    private static void ValidateTotals(WeightRegisterSheet sheet)
    {
        var occupiedColumns = sheet.Cells
            .Select(cell => cell.Column)
            .Distinct()
            .ToHashSet();
        var primaryTotalColumns = sheet.Totals
            .Where(total => total.Row == 1)
            .Select(total => total.Column)
            .ToHashSet();
        if (!occupiedColumns.IsSubsetOf(primaryTotalColumns))
        {
            throw new WeightRegisterRecognitionException(
                $"Лист «{sheet.Name}»: отсутствует рукописный весовой итог первой строки для занятой колонки.");
        }

        var suspectsWithoutReason = sheet.Totals
            .Where(total => total.Status == RegisterCellStatus.Suspect)
            .Any(total => string.IsNullOrWhiteSpace(total.Comment) || total.Comment.Length < 12);
        if (suspectsWithoutReason)
        {
            throw new WeightRegisterRecognitionException(
                $"Лист «{sheet.Name}»: для сомнительного итога не указано конкретное визуальное пояснение.");
        }
    }

    private static object CreateResponseSchema(RegisterTableShape table)
    {
        var cell = new
        {
            type = "object",
            additionalProperties = false,
            properties = new Dictionary<string, object>
            {
                ["row"] = new { type = "integer", minimum = 1, maximum = table.DataRows },
                ["column"] = new { type = "integer", minimum = 1, maximum = table.Columns },
                ["ocrText"] = new { type = new[] { "string", "null" } },
                ["status"] = new { type = "string", @enum = new[] { "Unreviewed", "Suspect", "Empty" } },
                ["comment"] = new { type = new[] { "string", "null" } },
                ["confidence"] = new { type = new[] { "number", "null" }, minimum = 0, maximum = 1 }
            },
            required = new[] { "row", "column", "ocrText", "status", "comment", "confidence" }
        };
        var total = new
        {
            type = "object",
            additionalProperties = false,
            properties = new Dictionary<string, object>
            {
                ["row"] = new { type = "integer", minimum = 1, maximum = table.TotalRows },
                ["column"] = new { type = "integer", minimum = 1, maximum = table.Columns },
                ["ocrText"] = new { type = new[] { "string", "null" } },
                ["status"] = new { type = "string", @enum = new[] { "Unreviewed", "Suspect", "Empty" } },
                ["comment"] = new { type = new[] { "string", "null" } }
            },
            required = new[] { "row", "column", "ocrText", "status", "comment" }
        };
        var anchor = new
        {
            type = "object",
            additionalProperties = false,
            properties = new Dictionary<string, object>
            {
                ["row"] = new { type = "integer", minimum = 1, maximum = table.DataRows },
                ["column"] = new { type = "integer", minimum = 1, maximum = table.Columns },
                ["filled"] = new { type = "boolean" },
                ["ocrText"] = new { type = new[] { "string", "null" } }
            },
            required = new[] { "row", "column", "filled", "ocrText" }
        };
        return new
        {
            type = "object",
            additionalProperties = false,
            properties = new Dictionary<string, object>
            {
                ["slaughterDate"] = new { type = new[] { "string", "null" } },
                ["cuttingDate"] = new { type = new[] { "string", "null" } },
                ["cells"] = new { type = "array", items = cell },
                ["totals"] = new { type = "array", items = total },
                ["verifiedAnchors"] = new { type = "array", items = anchor, minItems = 4, maxItems = 4 },
                ["filledCells"] = new { type = "integer", minimum = 0, maximum = table.DataRows * table.Columns },
                ["qualityNotes"] = new { type = "array", items = new { type = "string" } }
            },
            required = new[] { "slaughterDate", "cuttingDate", "cells", "totals", "verifiedAnchors", "filledCells", "qualityNotes" }
        };
    }

    private static string ExtractOutputText(string responseBody)
    {
        using var document = JsonDocument.Parse(responseBody);
        if (document.RootElement.TryGetProperty("output_text", out var direct)
            && direct.ValueKind == JsonValueKind.String)
        {
            return direct.GetString()!;
        }

        if (document.RootElement.TryGetProperty("output", out var output)
            && output.ValueKind == JsonValueKind.Array)
        {
            var builder = new StringBuilder();
            foreach (var item in output.EnumerateArray())
            {
                if (!item.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }
                foreach (var part in content.EnumerateArray())
                {
                    var type = part.TryGetProperty("type", out var typeValue) ? typeValue.GetString() : null;
                    if (type == "refusal")
                    {
                        throw new WeightRegisterRecognitionException("Модель отказалась обрабатывать изображение.");
                    }
                    if (type == "output_text" && part.TryGetProperty("text", out var text))
                    {
                        builder.Append(text.GetString());
                    }
                }
            }
            if (builder.Length > 0)
            {
                return builder.ToString();
            }
        }

        throw new WeightRegisterRecognitionException("Сервис распознавания не вернул текст результата.");
    }

    private static string ExtractApiError(string responseBody)
    {
        try
        {
            using var document = JsonDocument.Parse(responseBody);
            if (document.RootElement.TryGetProperty("error", out var error)
                && error.ValueKind == JsonValueKind.Object)
            {
                var code = error.TryGetProperty("code", out var codeElement)
                    ? codeElement.GetString()
                    : null;
                var type = error.TryGetProperty("type", out var typeElement)
                    ? typeElement.GetString()
                    : null;
                var safeParts = new[] { type, code }
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                if (safeParts.Length > 0)
                {
                    return string.Join(" / ", safeParts);
                }
            }
        }
        catch (JsonException)
        {
            // Ниже возвращается нейтральное сообщение без тела неизвестного ответа.
        }
        return "неизвестная ошибка API";
    }

    private static string? JoinComments(params string?[] comments)
    {
        var values = comments
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return values.Length == 0 ? null : string.Join("; ", values);
    }

    private sealed class RecognizedSheetPayload
    {
        public string? SlaughterDate { get; set; }
        public string? CuttingDate { get; set; }
        public List<RecognizedCellPayload> Cells { get; set; } = new();
        public List<RecognizedTotalPayload> Totals { get; set; } = new();
        public List<RecognizedAnchorPayload> VerifiedAnchors { get; set; } = new();
        public int FilledCells { get; set; }
        public List<string> QualityNotes { get; set; } = new();
    }

    private sealed class RecognizedCellPayload
    {
        public int Row { get; set; }
        public int Column { get; set; }
        public string? OcrText { get; set; }
        public RecognizedCellStatus Status { get; set; }
        public string? Comment { get; set; }
        public double? Confidence { get; set; }
    }

    private sealed class RecognizedTotalPayload
    {
        public int Row { get; set; }
        public int Column { get; set; }
        public string? OcrText { get; set; }
        public RecognizedCellStatus Status { get; set; }
        public string? Comment { get; set; }
    }

    private sealed class RecognizedAnchorPayload
    {
        public int Row { get; set; }
        public int Column { get; set; }
        public bool Filled { get; set; }
        public string? OcrText { get; set; }
    }

    private sealed record PreparedReviewBlock(int FirstRow, int LastRow, MemoryStream Image);

    private sealed class PreparedScanImages : IDisposable
    {
        public PreparedScanImages(
            MemoryStream dateHeader,
            MemoryStream tableOverview,
            IReadOnlyList<PreparedReviewBlock> reviewBlocks,
            MemoryStream totals)
        {
            DateHeader = dateHeader;
            TableOverview = tableOverview;
            ReviewBlocks = reviewBlocks;
            Totals = totals;
        }

        public MemoryStream DateHeader { get; }
        public MemoryStream TableOverview { get; }
        public IReadOnlyList<PreparedReviewBlock> ReviewBlocks { get; }
        public MemoryStream Totals { get; }

        public void Dispose()
        {
            DateHeader.Dispose();
            TableOverview.Dispose();
            foreach (var block in ReviewBlocks)
            {
                block.Image.Dispose();
            }
            Totals.Dispose();
        }
    }

    [JsonConverter(typeof(JsonStringEnumConverter))]
    private enum RecognizedCellStatus
    {
        Unreviewed,
        Suspect,
        Empty
    }
}

/// <summary>Безопасная для UI ошибка настройки или качества распознавания.</summary>
public sealed class WeightRegisterRecognitionException : Exception
{
    public WeightRegisterRecognitionException(string message)
        : base(message)
    {
    }

    public WeightRegisterRecognitionException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
