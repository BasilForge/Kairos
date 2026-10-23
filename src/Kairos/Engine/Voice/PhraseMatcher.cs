namespace Kairos.Engine.Voice;

/// <summary>Compares sequences of speech embeddings with subsequence dynamic time warping.</summary>
public static class PhraseMatcher
{
    /// <summary>
    /// Cheapest cosine-distance alignment of the whole <paramref name="template"/> to any stretch of
    /// <paramref name="query"/>, divided by the template length. Steps (1,1), (1,2) and (2,1) allow speaking
    /// up to twice as fast or slow without letting one frame absorb many. <paramref name="maxStart"/>: the stretch starts
    /// within the query's first frames (the phrase said first, not further in among words).
    /// </summary>
    public static double Cost(float[][] template, float[][] query, int maxStart = int.MaxValue)
    {
        int n = template.Length, m = query.Length;
        if (n == 0 || m < n / 2)
            return double.PositiveInfinity;
        var t = Normalize(template);
        var q = Normalize(query);

        var d = new double[n, m];
        for (int j = 0; j < m; j++)
            d[0, j] = j < maxStart ? Distance(t[0], q[j]) : double.PositiveInfinity;
        for (int i = 1; i < n; i++)
        {
            for (int j = 0; j < m; j++)
            {
                double best = double.PositiveInfinity;
                if (j >= 1)
                {
                    best = Math.Min(best, d[i - 1, j - 1]);
                    if (i >= 2)
                        best = Math.Min(best, d[i - 2, j - 1]);
                }
                if (j >= 2)
                    best = Math.Min(best, d[i - 1, j - 2]);
                d[i, j] = double.IsPositiveInfinity(best) ? best : best + Distance(t[i], q[j]);
            }
        }

        double end = double.PositiveInfinity;
        for (int j = 0; j < m; j++)
            end = Math.Min(end, d[n - 1, j]);
        return end / n;
    }

    /// <summary>Mean cost of the two templates that fit best (one template: its cost).</summary>
    public static double Score(IReadOnlyList<float[][]> templates, float[][] query, int maxStart = int.MaxValue)
    {
        double first = double.PositiveInfinity, second = double.PositiveInfinity;
        foreach (var t in templates)
        {
            double c = Cost(t, query, maxStart);
            if (c < first)
            {
                second = first;
                first = c;
            }
            else if (c < second)
            {
                second = c;
            }
        }
        return double.IsPositiveInfinity(second) ? first : (first + second) / 2;
    }

    /// <summary>0..1 for the UI: full at 0.6× the threshold, 0.6 at the threshold, empty at 1.6×.</summary>
    public static double Strength(double score, double threshold) =>
        Math.Clamp((1.6 * threshold - score) / threshold, 0, 1);

    static float[][] Normalize(float[][] seq)
    {
        var result = new float[seq.Length][];
        for (int i = 0; i < seq.Length; i++)
        {
            double norm = 0;
            foreach (float v in seq[i])
                norm += v * v;
            float scale = (float)(1 / (Math.Sqrt(norm) + 1e-6));
            result[i] = [.. seq[i].Select(v => v * scale)];
        }
        return result;
    }

    static double Distance(float[] a, float[] b)
    {
        double dot = 0;
        for (int k = 0; k < a.Length; k++)
            dot += a[k] * b[k];
        return 1 - dot;
    }
}
