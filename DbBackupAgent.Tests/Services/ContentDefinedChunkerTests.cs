using System.Security.Cryptography;
using DbBackupAgent.Services;

namespace DbBackupAgent.Tests.Services;

[TestFixture]
public sealed class ContentDefinedChunkerTests
{
    private ContentDefinedChunker _chunker = null!;

    [SetUp]
    public void SetUp() => _chunker = new ContentDefinedChunker();

    [Test]
    public void Split_EmptyStream_ReturnsNoChunks()
    {
        using var stream = new MemoryStream([]);

        var chunks = _chunker.Split(stream).ToList();

        Assert.That(chunks, Is.Empty);
    }

    [Test]
    public void Split_StreamSmallerThanMinSize_ReturnsSingleChunkOfInputLength()
    {
        var data = GenerateDeterministicBytes(ContentDefinedChunker.MinSize / 2, seed: 1);
        using var stream = new MemoryStream(data);

        var chunks = _chunker.Split(stream).ToList();

        Assert.That(chunks, Has.Count.EqualTo(1));
        Assert.That(chunks[0], Has.Length.EqualTo(data.Length));
        Assert.That(chunks[0], Is.EqualTo(data));
    }

    [Test]
    public void Split_IsDeterministic_SameInputProducesSameBoundaries()
    {
        var data = GenerateDeterministicBytes(ContentDefinedChunker.MaxSize * 3, seed: 42);

        var firstRun = _chunker.Split(new MemoryStream(data)).Select(c => c.Length).ToList();
        var secondRun = new ContentDefinedChunker().Split(new MemoryStream(data)).Select(c => c.Length).ToList();

        Assert.That(secondRun, Is.EqualTo(firstRun));
    }

    [Test]
    public void Split_ConcatenatedChunks_ReproduceOriginalInput()
    {
        var data = GenerateDeterministicBytes(ContentDefinedChunker.MaxSize * 2 + 123_456, seed: 7);

        var reassembled = _chunker.Split(new MemoryStream(data)).SelectMany(c => c).ToArray();

        Assert.That(reassembled, Is.EqualTo(data));
    }

    [Test]
    public void Split_AllChunksRespectSizeBounds_ExceptLastMayBeShorter()
    {
        var data = GenerateDeterministicBytes(ContentDefinedChunker.MaxSize * 5, seed: 99);

        var chunks = _chunker.Split(new MemoryStream(data)).ToList();

        for (int i = 0; i < chunks.Count - 1; i++)
        {
            Assert.That(chunks[i].Length, Is.InRange(ContentDefinedChunker.MinSize, ContentDefinedChunker.MaxSize),
                $"chunk {i} is outside size bounds");
        }
        Assert.That(chunks[^1].Length, Is.LessThanOrEqualTo(ContentDefinedChunker.MaxSize));
    }

    [Test]
    public void Split_InsertingByteAtStart_SharesMostChunksWithOriginal()
    {
        var original = GenerateDeterministicBytes(ContentDefinedChunker.MaxSize * 4, seed: 13);
        var modified = new byte[original.Length + 1];
        modified[0] = 0xAB;
        Buffer.BlockCopy(original, 0, modified, 1, original.Length);

        var originalHashes = _chunker.Split(new MemoryStream(original))
            .Select(Sha256Hex).ToHashSet();
        var modifiedHashes = new ContentDefinedChunker().Split(new MemoryStream(modified))
            .Select(Sha256Hex).ToHashSet();

        var shared = originalHashes.Intersect(modifiedHashes).Count();

        Assert.That(shared, Is.GreaterThan(originalHashes.Count / 2),
            "CDC should keep most chunks identical after a single-byte prepend");
    }

    private static byte[] GenerateDeterministicBytes(int length, int seed)
    {
        var random = new Random(seed);
        var buffer = new byte[length];
        random.NextBytes(buffer);
        return buffer;
    }

    private static string Sha256Hex(byte[] data) =>
        Convert.ToHexString(SHA256.HashData(data));
}
