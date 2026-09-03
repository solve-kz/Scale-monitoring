using Microsoft.Extensions.Options;
using Scalemon.WebApp.Models;
using static Scalemon.WebApp.Models.WeighingModels;

namespace Scalemon.WebApp.Data;

/// <summary>Формирует ссылку на архив конкретной модели видеорегистратора.</summary>
public interface IVideoArchiveService
{
    /// <summary>Возвращает фрагмент, начинающийся раньше времени взвешивания на настроенное число секунд.</summary>
    VideoArchiveClip GetClip(Weighing weighing);
}

/// <summary>Конфигурируемый адаптер видеоархива; конкретный URL включается после уточнения модели регистратора.</summary>
public sealed class ConfiguredVideoArchiveService : IVideoArchiveService
{
    private readonly VideoArchiveOptions _options;

    public ConfiguredVideoArchiveService(IOptions<WeightRegisterReviewOptions> options)
    {
        _options = options.Value.VideoArchive ?? new VideoArchiveOptions();
    }

    /// <inheritdoc />
    public VideoArchiveClip GetClip(Weighing weighing)
    {
        var preRoll = Math.Clamp(_options.PreRollSeconds, 0, 60);
        var start = weighing.Timestamp.AddSeconds(-preRoll);
        if (!_options.Enabled || string.IsNullOrWhiteSpace(_options.UrlTemplate))
        {
            return new VideoArchiveClip(
                false,
                start,
                preRoll,
                null,
                "Видеорегистратор ещё не настроен. Укажите модель и шаблон URL архива в WeightRegisterReview:VideoArchive.");
        }

        var url = _options.UrlTemplate
            .Replace("{weighingId}", weighing.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("{start}", Uri.EscapeDataString(start.ToString("O")), StringComparison.Ordinal)
            .Replace("{timestamp}", Uri.EscapeDataString(weighing.Timestamp.ToString("O")), StringComparison.Ordinal);
        return new VideoArchiveClip(true, start, preRoll, url, "Архив подготовлен.");
    }
}
