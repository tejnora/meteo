namespace Meteo.Core.Geo;

/// <summary>
/// Regular projected grid (UTM 33N metres). Cell (i, j) has its centre at
/// (X0 + (i + 0.5) * CellSize, Y0 + (j + 0.5) * CellSize); j grows to the north.
/// </summary>
public sealed record GridDefinition(double X0, double Y0, double CellSize, int Nx, int Ny)
{
    /// <summary>Default 5 x 5 km forecast grid covering the Czech Republic with a ~20 km margin.</summary>
    public static GridDefinition CzechRepublic5Km { get; } = new(270_000, 5_355_000, 5_000, 106, 65);

    public double X1 => X0 + Nx * CellSize;
    public double Y1 => Y0 + Ny * CellSize;

    public double CenterX(int i) => X0 + (i + 0.5) * CellSize;
    public double CenterY(int j) => Y0 + (j + 0.5) * CellSize;

    /// <summary>Returns a grid with another cell size over the same extent.</summary>
    public GridDefinition WithCellSize(double cellSize) =>
        new(X0, Y0, cellSize, (int)Math.Round((X1 - X0) / cellSize), (int)Math.Round((Y1 - Y0) / cellSize));
}
