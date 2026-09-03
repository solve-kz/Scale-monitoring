using Microsoft.Extensions.Options;
using Scalemon.WebApp.Models;
using static Scalemon.WebApp.Models.WeighingModels;

namespace Scalemon.WebApp.Data;

/// <summary>
/// Выравнивает последовательности ручных и автоматических взвешиваний без сдвига всего остатка дня.
/// </summary>
public sealed class WeightRegisterComparisonEngine
{
    // Пропуск означает отсутствие целой записи и должен быть существенно дороже
    // обычного расхождения веса. Иначе случайные похожие массы разбивают весь день
    // на сотни ложных пар ManualOnly/AutomaticOnly.
    private const double GapCost = 3d;
    private readonly decimal _toleranceKg;

    public WeightRegisterComparisonEngine(IOptions<WeightRegisterReviewOptions> options)
    {
        _toleranceKg = options.Value.ComparisonToleranceKg > 0
            ? options.Value.ComparisonToleranceKg
            : 0.04m;
    }

    /// <summary>
    /// Сопоставляет заполненные ручные ячейки с автоматическими записями в порядке колонок по 40 строк.
    /// </summary>
    public RegisterComparisonResult Compare(
        WeightRegisterProject project,
        IReadOnlyList<Weighing> automaticWeighings)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(automaticWeighings);

        var manual = project.Sheets
            .SelectMany(sheet => sheet.Cells
                .Where(cell => cell.Row >= 1 && cell.Row <= sheet.Table.DataRows)
                .Where(cell => cell.Column >= 1 && cell.Column <= sheet.Table.Columns)
                .Where(cell => cell.EffectiveValue.HasValue)
                .OrderBy(cell => cell.Column)
                .ThenBy(cell => cell.Row)
                .Select(cell => new ManualSequenceCell(
                    new RegisterCellKey(sheet.Id, cell.Row, cell.Column),
                    cell)))
            .ToArray();

        var automatic = automaticWeighings.OrderBy(item => item.Timestamp).ToArray();
        var operations = Align(manual, automatic);
        var result = new RegisterComparisonResult();

        foreach (var operation in operations)
        {
            if (operation.Manual is null)
            {
                result.AutomaticOnlyCount++;
                continue;
            }

            var comparison = new RegisterCellComparison
            {
                Key = operation.Manual.Key,
                Manual = operation.Manual.Cell,
                Automatic = operation.Automatic,
                Kind = operation.Automatic is null
                    ? RegisterComparisonKind.ManualOnly
                    : Math.Abs(operation.Manual.Cell.EffectiveValue!.Value - operation.Automatic.Weight) <= _toleranceKg
                        ? RegisterComparisonKind.WithinTolerance
                        : RegisterComparisonKind.Difference
            };
            result.Cells[comparison.Key] = comparison;
            if (comparison.Kind == RegisterComparisonKind.ManualOnly)
            {
                result.ManualOnlyCount++;
            }
        }

        var nextManual = new ManualSequenceCell?[operations.Count];
        ManualSequenceCell? upcomingManual = null;
        for (var index = operations.Count - 1; index >= 0; index--)
        {
            nextManual[index] = upcomingManual;
            if (operations[index].Manual is not null)
            {
                upcomingManual = operations[index].Manual;
            }
        }

        ManualSequenceCell? previousManual = null;
        for (var index = 0; index < operations.Count; index++)
        {
            var gap = operations[index];
            if (gap.Manual is not null)
            {
                previousManual = gap.Manual;
                continue;
            }
            if (gap.Automatic is null)
            {
                continue;
            }

            var before = previousManual;
            var after = nextManual[index];
            if (before is null || after is null)
            {
                continue;
            }

            MarkGapNeighbor(before, gap.Automatic);
            if (after.Key != before.Key)
            {
                MarkGapNeighbor(after, gap.Automatic);
            }
        }

        foreach (var uncertain in project.Sheets.SelectMany(sheet => sheet.Cells
                     .Where(cell => !cell.EffectiveValue.HasValue)
                     .Where(cell => cell.Status != RegisterCellStatus.Empty)
                     .Where(cell => cell.Status == RegisterCellStatus.Suspect || !string.IsNullOrWhiteSpace(cell.OcrText))
                     .Select(cell => new ManualSequenceCell(
                         new RegisterCellKey(sheet.Id, cell.Row, cell.Column),
                         cell))))
        {
            result.Cells.TryAdd(uncertain.Key, new RegisterCellComparison
            {
                Key = uncertain.Key,
                Manual = uncertain.Cell,
                Kind = RegisterComparisonKind.Empty
            });
        }

        return result;

        void MarkGapNeighbor(ManualSequenceCell? neighbor, Weighing gapWeighing)
        {
            if (neighbor is null || !result.Cells.TryGetValue(neighbor.Key, out var comparison))
            {
                return;
            }

            comparison.Kind = RegisterComparisonKind.AutomaticOnlyBetween;
            comparison.AutomaticGaps.Add(gapWeighing);
        }
    }

    private List<AlignmentOperation> Align(
        IReadOnlyList<ManualSequenceCell> manual,
        IReadOnlyList<Weighing> automatic)
    {
        var rows = manual.Count + 1;
        var columns = automatic.Count + 1;
        var choices = new AlignmentChoice[rows, columns];
        var previousCosts = new double[columns];
        var currentCosts = new double[columns];

        for (var column = 1; column < columns; column++)
        {
            previousCosts[column] = column * GapCost;
            choices[0, column] = AlignmentChoice.AutomaticOnly;
        }

        for (var row = 1; row < rows; row++)
        {
            currentCosts[0] = row * GapCost;
            choices[row, 0] = AlignmentChoice.ManualOnly;
            for (var column = 1; column < columns; column++)
            {
                var match = previousCosts[column - 1] + MatchCost(
                    manual[row - 1].Cell.EffectiveValue!.Value,
                    automatic[column - 1].Weight);
                var manualOnly = previousCosts[column] + GapCost;
                var automaticOnly = currentCosts[column - 1] + GapCost;

                if (match <= manualOnly && match <= automaticOnly)
                {
                    currentCosts[column] = match;
                    choices[row, column] = AlignmentChoice.Match;
                }
                else if (automaticOnly <= manualOnly)
                {
                    currentCosts[column] = automaticOnly;
                    choices[row, column] = AlignmentChoice.AutomaticOnly;
                }
                else
                {
                    currentCosts[column] = manualOnly;
                    choices[row, column] = AlignmentChoice.ManualOnly;
                }
            }

            (previousCosts, currentCosts) = (currentCosts, previousCosts);
        }

        var operations = new List<AlignmentOperation>(Math.Max(manual.Count, automatic.Count));
        var currentRow = manual.Count;
        var currentColumn = automatic.Count;
        while (currentRow > 0 || currentColumn > 0)
        {
            var choice = choices[currentRow, currentColumn];
            if (currentRow > 0 && currentColumn > 0 && choice == AlignmentChoice.Match)
            {
                operations.Add(new AlignmentOperation(manual[currentRow - 1], automatic[currentColumn - 1]));
                currentRow--;
                currentColumn--;
            }
            else if (currentColumn > 0 && (currentRow == 0 || choice == AlignmentChoice.AutomaticOnly))
            {
                operations.Add(new AlignmentOperation(null, automatic[currentColumn - 1]));
                currentColumn--;
            }
            else
            {
                operations.Add(new AlignmentOperation(manual[currentRow - 1], null));
                currentRow--;
            }
        }

        operations.Reverse();
        return operations;
    }

    private double MatchCost(decimal manual, decimal automatic)
    {
        var difference = Math.Abs(manual - automatic);
        if (difference <= _toleranceKg)
        {
            return (double)(difference / _toleranceKg) * 0.05d;
        }

        return 0.75d + Math.Min(1d, (double)difference);
    }

    private sealed record ManualSequenceCell(RegisterCellKey Key, RegisterReviewCell Cell);
    private sealed record AlignmentOperation(ManualSequenceCell? Manual, Weighing? Automatic);

    private enum AlignmentChoice : byte
    {
        Match,
        ManualOnly,
        AutomaticOnly
    }
}

/// <summary>Вычисляет crop ячеек по совместимой с WeightRegisterReviewApp калибровке.</summary>
public static class WeightRegisterGeometry
{
    /// <summary>Создаёт стартовую калибровку, совпадающую с WeightRegisterReviewApp.</summary>
    public static RegisterTableCalibration CreateDefaultCalibration(int imageWidth, int imageHeight)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(imageWidth, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(imageHeight, 1);
        var scaleX = imageWidth / 850d;
        var scaleY = imageHeight / 1169d;
        return new RegisterTableCalibration
        {
            ImageWidth = imageWidth,
            ImageHeight = imageHeight,
            TableLeft = 42d * scaleX,
            TableTop = 112d * scaleY,
            TableRight = 767d * scaleX,
            TableBottom = 966d * scaleY,
            RotationDegrees = 0d
        };
    }

    /// <summary>Возвращает границы ячейки данных в пикселях исходного изображения.</summary>
    public static RegisterCellBounds GetCellBounds(WeightRegisterSheet sheet, int row, int column)
    {
        ArgumentNullException.ThrowIfNull(sheet);
        var calibration = sheet.Calibration
            ?? throw new InvalidOperationException("Лист ещё не откалиброван.");
        ArgumentOutOfRangeException.ThrowIfLessThan(row, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(row, sheet.Table.DataRows);
        ArgumentOutOfRangeException.ThrowIfLessThan(column, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(column, sheet.Table.Columns);

        var gridRows = sheet.Table.HeaderRows + sheet.Table.DataRows + sheet.Table.TotalRows;
        var gridColumns = sheet.Table.Columns + 1;
        var rowHeight = (calibration.TableBottom - calibration.TableTop) / gridRows;
        var columnWidth = (calibration.TableRight - calibration.TableLeft) / gridColumns;
        return new RegisterCellBounds(
            calibration.TableLeft + column * columnWidth,
            calibration.TableTop + (sheet.Table.HeaderRows + row - 1) * rowHeight,
            columnWidth,
            rowHeight);
    }

    /// <summary>Возвращает границы итоговой ячейки в пикселях исходного изображения.</summary>
    public static RegisterCellBounds GetTotalBounds(WeightRegisterSheet sheet, int totalRow, int column)
    {
        ArgumentNullException.ThrowIfNull(sheet);
        var calibration = sheet.Calibration
            ?? throw new InvalidOperationException("Лист ещё не откалиброван.");
        ArgumentOutOfRangeException.ThrowIfLessThan(totalRow, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(totalRow, sheet.Table.TotalRows);
        ArgumentOutOfRangeException.ThrowIfLessThan(column, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(column, sheet.Table.Columns);

        var gridRows = sheet.Table.HeaderRows + sheet.Table.DataRows + sheet.Table.TotalRows;
        var gridColumns = sheet.Table.Columns + 1;
        var rowHeight = (calibration.TableBottom - calibration.TableTop) / gridRows;
        var columnWidth = (calibration.TableRight - calibration.TableLeft) / gridColumns;
        return new RegisterCellBounds(
            calibration.TableLeft + column * columnWidth,
            calibration.TableTop
                + (sheet.Table.HeaderRows + sheet.Table.DataRows + totalRow - 1) * rowHeight,
            columnWidth,
            rowHeight);
    }
}
