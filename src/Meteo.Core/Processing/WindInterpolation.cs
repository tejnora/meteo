namespace Meteo.Core.Processing;

/// <summary>Vertical interpolation of model-level wind to a fixed height above mean sea level.</summary>
public static class WindInterpolation
{
    /// <param name="heightAmsl">Target height (m AMSL).</param>
    /// <param name="levels">Available full model levels.</param>
    /// <param name="fullHeight">Height AMSL of a full level per cell.</param>
    /// <param name="u">U component of a level per cell.</param>
    /// <param name="v">V component of a level per cell.</param>
    /// <param name="ground">Terrain height per cell; target below terrain gives NaN.</param>
    public static (float[] U, float[] V) ToHeight(int heightAmsl, IReadOnlyList<int> levels,
        Func<int, float[]> fullHeight, Func<int, float[]> u, Func<int, float[]> v, float[] ground)
    {
        int n = ground.Length;
        var zs = levels.Select(l => fullHeight(l)).ToArray();
        var us = levels.Select(u).ToArray();
        var vs = levels.Select(v).ToArray();
        var ru = new float[n];
        var rv = new float[n];
        double h = heightAmsl;

        for (int c = 0; c < n; c++)
        {
            if (!float.IsFinite(ground[c]) || h < ground[c]) { ru[c] = rv[c] = float.NaN; continue; }

            int below = -1, above = -1;
            double zb = double.NegativeInfinity, za = double.PositiveInfinity;
            for (int k = 0; k < zs.Length; k++)
            {
                double z = zs[k][c];
                if (!double.IsFinite(z)) continue;
                if (z <= h && z > zb) { zb = z; below = k; }
                if (z >= h && z < za) { za = z; above = k; }
            }

            if (below < 0 && above < 0) { ru[c] = rv[c] = float.NaN; continue; }
            if (below < 0) { ru[c] = us[above][c]; rv[c] = vs[above][c]; continue; }      // between ground and lowest level
            if (above < 0 || above == below) { ru[c] = us[below][c]; rv[c] = vs[below][c]; continue; }

            double t = (h - zb) / (za - zb);
            ru[c] = (float)(us[below][c] + t * (us[above][c] - us[below][c]));
            rv[c] = (float)(vs[below][c] + t * (vs[above][c] - vs[below][c]));
        }
        return (ru, rv);
    }
}
