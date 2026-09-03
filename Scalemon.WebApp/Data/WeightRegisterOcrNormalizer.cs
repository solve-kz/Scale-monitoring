using System.Globalization;

namespace Scalemon.WebApp.Data;

/// <summary>Результат детерминированной нормализации рукописного текста.</summary>
public sealed record WeightRegisterOcrNormalization(
    string RawText,
    string NormalizedText,
    decimal? Value,
    bool IsSuspect,
    string? Reason);

/// <summary>Применяет числовые правила навыка weight-register-scans без подгонки под суммы.</summary>
public static class WeightRegisterOcrNormalizer
{
    /// <summary>Преобразует текст ячейки тела таблицы в килограммы.</summary>
    public static WeightRegisterOcrNormalization NormalizeBody(string? rawText)
        => Normalize(rawText, total: false);

    /// <summary>Преобразует текст итоговой ячейки в килограммы.</summary>
    public static WeightRegisterOcrNormalization NormalizeTotal(string? rawText)
        => Normalize(rawText, total: true);

    private static WeightRegisterOcrNormalization Normalize(string? rawText, bool total)
    {
        var raw = rawText?.Trim() ?? string.Empty;
        if (raw.Length == 0)
        {
            return new WeightRegisterOcrNormalization(raw, raw, null, false, null);
        }

        if (raw.Contains('?'))
        {
            return Suspect(raw, raw, null, "в распознанном тексте остался знак неопределённости");
        }

        var normalized = raw.Replace(',', '.');
        string? reason = null;
        if (normalized.All(char.IsDigit))
        {
            if (normalized.Length == 1)
            {
                normalized += "00";
            }
            else if (normalized.Length >= 3 && normalized.EndsWith("00", StringComparison.Ordinal))
            {
                var corrected = $"{normalized[..^1]}6";
                reason = $"распознано недопустимое окончание 00; последняя цифра автоматически заменена на 6 ({normalized} → {corrected})";
                normalized = corrected;
            }
        }

        if (!decimal.TryParse(normalized, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed))
        {
            return Suspect(raw, normalized, null, "текст не удалось однозначно преобразовать в число");
        }

        decimal value;
        if (normalized.Contains('.'))
        {
            value = decimal.Round(parsed, 2, MidpointRounding.AwayFromZero);
        }
        else
        {
            if (parsed > int.MaxValue)
            {
                return Suspect(raw, normalized, null, "распознанное число выходит за допустимый диапазон");
            }

            var integer = decimal.ToInt32(parsed);
            value = total
                ? integer / 100m
                : integer < 10
                    ? integer
                    : integer <= 30
                        ? integer
                        : integer < 100
                            ? integer / 10m
                            : integer / 100m;
            value = decimal.Round(value, 2, MidpointRounding.AwayFromZero);
        }

        if (value * 100m % 2m != 0m)
        {
            reason = JoinReasons(
                reason,
                $"значение {value:0.00} нарушает шаг весов 0,02 кг: последняя сотая должна быть чётной");
        }

        return new WeightRegisterOcrNormalization(raw, normalized, value, reason is not null, reason);
    }

    private static WeightRegisterOcrNormalization Suspect(
        string raw,
        string normalized,
        decimal? value,
        string reason)
        => new(raw, normalized, value, true, reason);

    private static string JoinReasons(string? first, string second)
        => string.IsNullOrWhiteSpace(first) ? second : $"{first}; {second}";
}
