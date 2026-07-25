using Dapr.Client;
using Moq;
using Xunit;

public class DuplicatePhotoGuardTests
{
    private const string PhotoA = "data:image/png;base64,OCR:CloudSoft Inc|180||Cloud software subscription:180";
    private const string PhotoB = "data:image/png;base64,OCR:AdBoost Media|1800||";

    [Fact]
    public async Task FirstSubmission_IsNotADuplicate_AndGetsRecorded()
    {
        var daprMock = new Mock<DaprClient>();
        daprMock.Setup(c => c.GetStateAndETagAsync<bool?>("statestore", It.IsAny<string>(), null, null, default))
            .ReturnsAsync(((bool?)null, ""));
        daprMock.Setup(c => c.TrySaveStateAsync("statestore", It.IsAny<string>(), true, "", null, null, default))
            .ReturnsAsync(true);

        var result = await DuplicatePhotoGuard.IsDuplicateAsync(daprMock.Object, "statestore", PhotoA);

        Assert.False(result);
        daprMock.Verify(c => c.TrySaveStateAsync("statestore", DuplicatePhotoGuard.BuildKey(PhotoA), true, "", null, null, default), Times.Once);
    }

    [Fact]
    public async Task SamePhotoAgain_IsRejectedAsDuplicate()
    {
        var daprMock = new Mock<DaprClient>();
        daprMock.Setup(c => c.GetStateAndETagAsync<bool?>("statestore", It.IsAny<string>(), null, null, default))
            .ReturnsAsync(((bool?)true, "etag-1"));

        var result = await DuplicatePhotoGuard.IsDuplicateAsync(daprMock.Object, "statestore", PhotoA);

        Assert.True(result);
        daprMock.Verify(c => c.TrySaveStateAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool?>(), It.IsAny<string>(), null, null, default), Times.Never);
    }

    [Fact]
    public async Task LosingTheRecordingRace_IsTreatedAsDuplicate()
    {
        // Two submissions of the same photo land at the same instant: both read "unseen",
        // only one ETag write wins. The loser re-reads, finds the key recorded, and must
        // come back as a duplicate rather than both slipping through.
        var daprMock = new Mock<DaprClient>();
        daprMock.SetupSequence(c => c.GetStateAndETagAsync<bool?>("statestore", It.IsAny<string>(), null, null, default))
            .ReturnsAsync(((bool?)null, ""))
            .ReturnsAsync(((bool?)true, "etag-1"));
        daprMock.Setup(c => c.TrySaveStateAsync("statestore", It.IsAny<string>(), true, "", null, null, default))
            .ReturnsAsync(false);

        var result = await DuplicatePhotoGuard.IsDuplicateAsync(daprMock.Object, "statestore", PhotoA);

        Assert.True(result);
    }

    [Fact]
    public void BuildKey_DifferentPhotos_ProduceDifferentKeys()
    {
        var key1 = DuplicatePhotoGuard.BuildKey(PhotoA);
        var key2 = DuplicatePhotoGuard.BuildKey(PhotoB);

        Assert.NotEqual(key1, key2);
    }

    [Fact]
    public void BuildKey_SamePhotoBytes_ProduceTheSameKey()
    {
        var key1 = DuplicatePhotoGuard.BuildKey(PhotoA);
        var key2 = DuplicatePhotoGuard.BuildKey(PhotoA);

        Assert.Equal(key1, key2);
    }

    [Fact]
    public void BuildKey_EvenAOneCharacterDifference_ProducesADifferentKey()
    {
        // Exact hash, on purpose (see the class doc comment): a retaken photo of the same
        // physical receipt is a different file and is expected to slip past this guard.
        var key1 = DuplicatePhotoGuard.BuildKey(PhotoA);
        var key2 = DuplicatePhotoGuard.BuildKey(PhotoA + "x");

        Assert.NotEqual(key1, key2);
    }
}
