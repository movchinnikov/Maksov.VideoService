using Maksov.VideoService.Friends.Bll;

namespace Maksov.VideoService.Friends.Tests;

public sealed class GraphEmbeddingTests
{
    private static readonly string[] Shared = ["x1", "x2", "x3", "x4"];
    private static readonly string[] Others = ["y1", "y2", "y3", "y4"];

    [Fact]
    public void Base_vector_is_deterministic_normalised_and_has_32_dimensions()
    {
        var a = GraphEmbedding.Base("alice");
        var b = GraphEmbedding.Base("alice");

        Assert.Equal(GraphEmbedding.Dimensions, a.Length);
        Assert.Equal(a, b);
        Assert.Equal(1.0, GraphEmbedding.Cosine(a, a), 5);
    }

    [Fact]
    public void People_following_the_same_accounts_are_more_similar_than_strangers()
    {
        var alice = GraphEmbedding.Compute("alice", Shared);
        var bob = GraphEmbedding.Compute("bob", Shared);
        var stranger = GraphEmbedding.Compute("stranger", Others);

        Assert.True(GraphEmbedding.Cosine(alice, bob) > GraphEmbedding.Cosine(alice, stranger));
    }

    [Fact]
    public void Vector_literal_uses_invariant_culture()
    {
        var literal = GraphEmbedding.ToVectorLiteral(GraphEmbedding.Base("alice"));

        Assert.StartsWith("[", literal, StringComparison.Ordinal);
        Assert.EndsWith("]", literal, StringComparison.Ordinal);
        Assert.Equal(GraphEmbedding.Dimensions - 1, literal.Count(c => c == ','));
        Assert.DoesNotContain(" ", literal, StringComparison.Ordinal);
    }
}
