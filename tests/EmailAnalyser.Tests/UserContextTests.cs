using EmailAnalyser.Core;
using Xunit;

namespace EmailAnalyser.Tests;

public class UserContextTests
{
    [Fact]
    public void Context_is_appended_to_the_system_prompt_and_changes_the_version()
    {
        var plain = PromptTemplate.LoadEmbedded();
        var withContext = PromptTemplate.LoadEmbedded(["Perheyhtiö on Esimerkki Sukuyhtiö Oy.", "  ", "Kohdeyhtiöt: Startup Oy​."]);

        Assert.Contains("- Perheyhtiö on Esimerkki Sukuyhtiö Oy.", withContext.System);
        Assert.Contains("- Kohdeyhtiöt: Startup Oy.", withContext.System);
        Assert.DoesNotContain("- \n", withContext.System);
        Assert.NotEqual(plain.Version, withContext.Version);
        Assert.Equal(plain.Version, PromptTemplate.LoadEmbedded([]).Version);
        Assert.StartsWith(PromptTemplate.Name, withContext.Version);
    }

    [Fact]
    public void Overlong_context_is_refused() =>
        Assert.Throws<InvalidOperationException>(() => PromptTemplate.LoadEmbedded([new string('x', PromptTemplate.MaxUserContextChars + 1)]));

    [Fact]
    public void Every_topic_is_in_the_prompt_and_the_schema()
    {
        var prompt = PromptTemplate.LoadEmbedded();
        foreach (var topic in Labels.Topics)
        {
            Assert.Contains($"- {topic}:", prompt.System);
            Assert.Contains($"\"{topic}\"", prompt.SchemaJson);
        }
    }

    [Fact]
    public void Preheader_padding_is_removed_before_the_model()
    {
        var padding = string.Concat(Enumerable.Repeat("͏ ­ ", 200));
        var prepared = TextPreparation.Prepare("Black Week " + padding + "alkaa huomenna.");
        Assert.Equal("Black Week alkaa huomenna.", prepared.Text);
    }
}
