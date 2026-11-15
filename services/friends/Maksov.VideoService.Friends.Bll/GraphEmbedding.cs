using System.Security.Cryptography;
using System.Text;

namespace Maksov.VideoService.Friends.Bll;

/// <summary>
/// Baseline graph embedding <c>graph-hash-v1</c> (deterministic, local, no ML model; real models arrive in lessons 8-9).
/// Every user has a stable pseudo-random base vector <c>h(user)</c>; a user's embedding is
/// <c>normalize(h(user) + 0.5 * sum(h(f) for f in following))</c>, so people following the same accounts get similar vectors.
/// </summary>
public static class GraphEmbedding
{
    public const string ModelVersion = "graph-hash-v1";
    public const int Dimensions = 32;
    private const float NeighbourWeight = 0.5f;

    /// <summary>Stable base vector of a user: SHA-256 expanded to 32 floats in [-1, 1], L2-normalised.</summary>
    public static float[] Base(string userId)
    {
        ArgumentNullException.ThrowIfNull(userId);
        Span<byte> hash = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(Encoding.UTF8.GetBytes(ModelVersion + ":" + userId), hash);

        var vector = new float[Dimensions];
        for (var i = 0; i < Dimensions; i++)
        {
            vector[i] = (hash[i] - 127.5f) / 127.5f;
        }

        return Normalize(vector);
    }

    /// <summary>Embedding of a user from the accounts the user follows.</summary>
    public static float[] Compute(string userId, IEnumerable<string> followingIds)
    {
        ArgumentNullException.ThrowIfNull(followingIds);
        var vector = Base(userId);
        foreach (var followee in followingIds)
        {
            var neighbour = Base(followee);
            for (var i = 0; i < Dimensions; i++)
            {
                vector[i] += NeighbourWeight * neighbour[i];
            }
        }

        return Normalize(vector);
    }

    public static double Cosine(float[] a, float[] b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        double dot = 0;
        for (var i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
        }

        return dot;
    }

    /// <summary>pgvector text form: <c>[0.1,0.2,...]</c> (invariant culture).</summary>
    public static string ToVectorLiteral(float[] vector)
    {
        ArgumentNullException.ThrowIfNull(vector);
        return "[" + string.Join(',', vector.Select(static v => v.ToString("R", System.Globalization.CultureInfo.InvariantCulture))) + "]";
    }

    private static float[] Normalize(float[] vector)
    {
        double sum = 0;
        foreach (var v in vector)
        {
            sum += v * v;
        }

        var norm = (float)Math.Sqrt(sum);
        if (norm > 0)
        {
            for (var i = 0; i < vector.Length; i++)
            {
                vector[i] /= norm;
            }
        }

        return vector;
    }
}
