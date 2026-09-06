using Microsoft.Extensions.Options;
using Scalemon.WebApp.Data;
using Scalemon.WebApp.Models;
using static Scalemon.WebApp.Models.WeighingModels;

namespace Scalemon.CSTests;

public sealed class WeightRegisterComparisonEngineTests
{
    [Fact]
    public void DifferenceOfFortyGramsIsWithinTolerance()
    {
        var project = ProjectWithValues(8.00m);
        var result = CreateEngine().Compare(
            project,
            new[] { new Weighing(1, 8.04m, new DateTime(2026, 8, 26, 8, 0, 0)) });

        Assert.Equal(
            RegisterComparisonKind.WithinTolerance,
            result.Cells[new RegisterCellKey("sheet-1", 1, 1)].Kind);
    }

    [Fact]
    public void ExtraAutomaticWeighingMarksBothManualNeighborsOrange()
    {
        var project = ProjectWithValues(8.00m, 9.00m);
        var automatic = new[]
        {
            new Weighing(1, 8.00m, new DateTime(2026, 8, 26, 8, 0, 0)),
            new Weighing(2, 7.50m, new DateTime(2026, 8, 26, 8, 0, 1)),
            new Weighing(3, 9.00m, new DateTime(2026, 8, 26, 8, 0, 2))
        };

        var result = CreateEngine().Compare(project, automatic);

        Assert.Equal(1, result.AutomaticOnlyCount);
        Assert.Equal(RegisterComparisonKind.AutomaticOnlyBetween, result.Cells[new RegisterCellKey("sheet-1", 1, 1)].Kind);
        Assert.Equal(RegisterComparisonKind.AutomaticOnlyBetween, result.Cells[new RegisterCellKey("sheet-1", 2, 1)].Kind);
    }

    [Fact]
    public void ExtraAutomaticWeighingAtDayBoundaryDoesNotMarkOneCellOrange()
    {
        var project = ProjectWithValues(8.00m);
        var automatic = new[]
        {
            new Weighing(1, 7.50m, new DateTime(2026, 8, 26, 7, 59, 59)),
            new Weighing(2, 8.00m, new DateTime(2026, 8, 26, 8, 0, 0))
        };

        var result = CreateEngine().Compare(project, automatic);

        Assert.Equal(1, result.AutomaticOnlyCount);
        Assert.Equal(RegisterComparisonKind.WithinTolerance, result.Cells[new RegisterCellKey("sheet-1", 1, 1)].Kind);
    }

    [Fact]
    public void ManualValueWithoutAutomaticPairIsRedState()
    {
        var project = ProjectWithValues(8.00m, 9.00m);

        var result = CreateEngine().Compare(
            project,
            new[] { new Weighing(1, 8.00m, new DateTime(2026, 8, 26, 8, 0, 0)) });

        Assert.Equal(1, result.ManualOnlyCount);
        Assert.Equal(RegisterComparisonKind.ManualOnly, result.Cells[new RegisterCellKey("sheet-1", 2, 1)].Kind);
    }

    [Fact]
    public void AlternatingDifferentWeightsDoNotCreateFalseMissingPairs()
    {
        var project = ProjectWithValues(8.00m, 9.00m, 8.00m);
        var automatic = new[]
        {
            new Weighing(1, 9.00m, new DateTime(2026, 8, 26, 8, 0, 0)),
            new Weighing(2, 8.00m, new DateTime(2026, 8, 26, 8, 0, 1)),
            new Weighing(3, 9.00m, new DateTime(2026, 8, 26, 8, 0, 2))
        };

        var result = CreateEngine().Compare(project, automatic);

        Assert.Equal(0, result.ManualOnlyCount);
        Assert.Equal(0, result.AutomaticOnlyCount);
        Assert.All(result.Cells.Values, comparison => Assert.NotNull(comparison.Automatic));
    }

    [Fact]
    public void CorrectedValueIsComparedAgainOnNextCalculation()
    {
        var project = ProjectWithValues(8.20m);
        var automatic = new[]
        {
            new Weighing(1, 8.00m, new DateTime(2026, 8, 26, 8, 0, 0))
        };
        var key = new RegisterCellKey("sheet-1", 1, 1);

        var before = CreateEngine().Compare(project, automatic);
        project.Sheets[0].Cells[0].CorrectedValue = 8.02m;
        var after = CreateEngine().Compare(project, automatic);

        Assert.Equal(RegisterComparisonKind.Difference, before.Cells[key].Kind);
        Assert.Equal(RegisterComparisonKind.WithinTolerance, after.Cells[key].Kind);
    }

    [Fact]
    public void UnparsedSuspectCellRemainsSelectableForCorrection()
    {
        var project = ProjectWithValues(8.00m);
        project.Sheets[0].Cells.Add(new RegisterReviewCell
        {
            Row = 2,
            Column = 1,
            OcrText = "7?6",
            Status = RegisterCellStatus.Suspect,
            Comment = "средняя цифра неразборчива"
        });

        var result = CreateEngine().Compare(
            project,
            new[] { new Weighing(1, 8.00m, new DateTime(2026, 8, 26, 8, 0, 0)) });

        var suspect = result.Cells[new RegisterCellKey("sheet-1", 2, 1)];
        Assert.Equal(RegisterComparisonKind.Empty, suspect.Kind);
        Assert.Equal(RegisterCellStatus.Suspect, suspect.Manual.Status);
    }

    [Fact]
    public void ExplicitlyClearedCellIsNotDisplayedAsRawOcrText()
    {
        var project = ProjectWithValues(8.00m);
        project.Sheets[0].Cells[0].Value = null;
        project.Sheets[0].Cells[0].OcrText = "800";
        project.Sheets[0].Cells[0].Status = RegisterCellStatus.Empty;

        var result = CreateEngine().Compare(project, Array.Empty<Weighing>());

        Assert.Empty(result.Cells);
    }

    [Fact]
    public void GeometryUsesLeadingNumberColumnAndTwoHeaderRows()
    {
        var sheet = new WeightRegisterSheet
        {
            Calibration = new RegisterTableCalibration
            {
                ImageWidth = 1100,
                ImageHeight = 4400,
                TableLeft = 0,
                TableTop = 0,
                TableRight = 1100,
                TableBottom = 4400
            }
        };

        var bounds = WeightRegisterGeometry.GetCellBounds(sheet, row: 1, column: 1);

        Assert.Equal(100d, bounds.Left);
        Assert.Equal(200d, bounds.Top);
        Assert.Equal(100d, bounds.Width);
        Assert.Equal(100d, bounds.Height);
    }

    [Fact]
    public void TotalBoundsPointToFortyFirstRegisterRow()
    {
        var sheet = new WeightRegisterSheet
        {
            Calibration = new RegisterTableCalibration
            {
                ImageWidth = 1100,
                ImageHeight = 4400,
                TableLeft = 0,
                TableTop = 0,
                TableRight = 1100,
                TableBottom = 4400
            }
        };

        var bounds = WeightRegisterGeometry.GetTotalBounds(sheet, totalRow: 1, column: 1);

        Assert.Equal(100d, bounds.Left);
        Assert.Equal(4200d, bounds.Top);
        Assert.Equal(100d, bounds.Width);
        Assert.Equal(100d, bounds.Height);
    }

    [Fact]
    public void DefaultCalibrationMatchesWeightRegisterReviewAppTemplate()
    {
        var calibration = WeightRegisterGeometry.CreateDefaultCalibration(850, 1169);

        Assert.Equal(42d, calibration.TableLeft);
        Assert.Equal(112d, calibration.TableTop);
        Assert.Equal(767d, calibration.TableRight);
        Assert.Equal(966d, calibration.TableBottom);
        Assert.Equal(0d, calibration.RotationDegrees);
    }

    [Fact]
    public void CellBoundsStayInCalibrationGridWhenImageIsRotated()
    {
        var sheet = new WeightRegisterSheet
        {
            Calibration = new RegisterTableCalibration
            {
                ImageWidth = 1100,
                ImageHeight = 4400,
                TableLeft = 0,
                TableTop = 0,
                TableRight = 1100,
                TableBottom = 4400,
                RotationDegrees = 3
            }
        };

        var bounds = WeightRegisterGeometry.GetCellBounds(sheet, row: 1, column: 1);

        Assert.Equal(new RegisterCellBounds(100d, 200d, 100d, 100d), bounds);
    }

    private static WeightRegisterComparisonEngine CreateEngine()
        => new(Options.Create(new WeightRegisterReviewOptions { ComparisonToleranceKg = 0.04m }));

    private static WeightRegisterProject ProjectWithValues(params decimal[] values)
    {
        var sheet = new WeightRegisterSheet { Id = "sheet-1", Name = "Лист 1" };
        for (var index = 0; index < values.Length; index++)
        {
            sheet.Cells.Add(new RegisterReviewCell
            {
                Row = index + 1,
                Column = 1,
                Value = values[index]
            });
        }

        return new WeightRegisterProject
        {
            CuttingDate = new DateOnly(2026, 8, 26),
            Sheets = new List<WeightRegisterSheet> { sheet }
        };
    }
}
