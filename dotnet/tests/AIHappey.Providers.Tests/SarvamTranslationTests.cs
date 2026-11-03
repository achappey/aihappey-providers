using AIHappey.Core.Providers.Sarvam;

namespace AIHappey.Tests;

public sealed class SarvamTranslationTests
{
    [Theory]
    [InlineData("mayura:v1/translate-to/hi-IN", 1000)]
    [InlineData("sarvam-translate:v1/translate/en-IN/to/hi-IN", 2000)]
    [InlineData("sarvam/mayura:v1/translate-to/hi-IN", 1000)]
    public void Translation_chunks_respect_the_model_limit_and_preserve_source(
        string model,
        int expectedLimit)
    {
        var source = string.Join(
            " ",
            Enumerable.Range(0, 30).Select(index => $"{index:D2} {new string('x', 120)}."));

        var chunks = SarvamProvider.SplitTranslationText(source, model);

        Assert.Equal(expectedLimit, SarvamProvider.GetTranslationCharacterLimit(model));
        Assert.True(chunks.Count > 1);
        Assert.All(chunks, chunk => Assert.InRange(chunk.Text.Length, 1, expectedLimit));
        Assert.Equal(source, string.Concat(chunks.Select(chunk => chunk.Text + chunk.SeparatorAfter)));
    }

    [Fact]
    public void Translation_chunks_do_not_split_combining_text_elements()
    {
        const string model = "mayura:v1/translate-to/hi-IN";
        var source = new string('a', 999) + "e\u0301" + new string('b', 5);

        var chunks = SarvamProvider.SplitTranslationText(source, model);

        Assert.Equal(new string('a', 999), chunks[0].Text);
        Assert.StartsWith("e\u0301", chunks[1].Text);
        Assert.Equal(source, string.Concat(chunks.Select(chunk => chunk.Text + chunk.SeparatorAfter)));
        Assert.All(chunks, chunk => Assert.True(chunk.Text.Length <= 1000));
    }
}
