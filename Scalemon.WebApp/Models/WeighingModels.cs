namespace Scalemon.WebApp.Models
{
    public class WeighingModels
    {
        public sealed record Weighing(int Id, decimal Weight, DateTime Timestamp);

        public sealed record Cell(int? Id, decimal? Weight, DateTime? Timestamp)
        {
            public bool HasValue => Id.HasValue && Weight.HasValue && Timestamp.HasValue;
        }

        public sealed record GridRow(Cell C1, Cell C2, Cell C3, Cell C4, Cell C5, Cell C6, Cell C7, Cell C8, Cell C9, Cell C10)
        {
            public IEnumerable<Cell> Cells => new[] { C1, C2, C3, C4, C5, C6, C7, C8, C9, C10 };
        }

        public sealed record GridRowVm(int No, GridRow Row);

        public sealed record DaySummary(int Count, decimal Sum, decimal? Min, decimal? Max, decimal? Avg);
    }
}
