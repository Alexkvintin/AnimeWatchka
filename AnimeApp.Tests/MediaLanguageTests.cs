using System.Globalization;
using AnimeApp.Services.Playback;
using Xunit;
namespace AnimeApp.Tests;
public class MediaLanguageTests
{
    [Theory]
    [InlineData("uk-UA","uk")]
    [InlineData("ru-RU","ru")]
    [InlineData("en-US","en")]
    [InlineData("de-DE","en")]
    public void UsesSystemLanguageOrEnglish(string culture,string expected) => Assert.Equal(expected,MediaLanguage.ForCulture(CultureInfo.GetCultureInfo(culture)));
}
