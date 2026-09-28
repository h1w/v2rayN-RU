namespace ServiceLib.Helper;

public static class ProfileColumnLayout
{
    public static bool IsRequiredStatisticsColumn(string name) =>
        name is nameof(ProfileItemModel.TotalDown) or nameof(ProfileItemModel.TotalUp)
            or nameof(ProfileItemModel.CurrentDown) or nameof(ProfileItemModel.CurrentUp);

    public static List<ColumnItem> Restore(IReadOnlyCollection<ColumnItem> saved, IReadOnlyCollection<ColumnItem> defaults)
    {
        var known = defaults.ToDictionary(column => column.Name, StringComparer.Ordinal);
        var result = saved.OrderBy(column => column.Index)
            .Where(column => known.ContainsKey(column.Name))
            .DistinctBy(column => column.Name)
            .Select(column => new ColumnItem { Name = column.Name, Width = column.Width })
            .ToList();
        foreach (var column in defaults)
        {
            if (result.All(item => item.Name != column.Name))
            {
                result.Add(new ColumnItem { Name = column.Name, Width = column.Width });
            }
        }

        // The presence of both rate columns distinguishes new layouts from legacy layouts.
        // Once migrated, retain subsequent user reordering while keeping required fields visible.
        if (!saved.Any(column => column.Name == nameof(ProfileItemModel.CurrentDown))
            || !saved.Any(column => column.Name == nameof(ProfileItemModel.CurrentUp)))
        {
            var statistics = defaults.Where(column => IsRequiredStatisticsColumn(column.Name))
                .Select(column => result.Single(item => item.Name == column.Name)).ToList();
            result.RemoveAll(column => IsRequiredStatisticsColumn(column.Name));
            var insertAt = result.FindIndex(column => column.Name == nameof(ProfileItemModel.SpeedVal)) + 1;
            result.InsertRange(insertAt, statistics);
        }

        for (var index = 0; index < result.Count; index++)
        {
            var column = result[index];
            column.Index = index;
            if (IsRequiredStatisticsColumn(column.Name))
            {
                column.Width = Math.Max(120, column.Width > 0 ? column.Width : known[column.Name].Width);
            }
        }
        return result;
    }
}
