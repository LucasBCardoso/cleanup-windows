using Cleanup.Platform.Windows;

namespace Cleanup.Platform.Tests;

public sealed class CurrentUserTests
{
    [Theory]
    [InlineData("Lucas Cardoso", "Lucas")]
    [InlineData("Cardoso, Lucas", "Lucas")]
    [InlineData("lucas.cardoso", "Lucas")]
    [InlineData("ana_souza", "Ana")]
    [InlineData("  maria  ", "Maria")]
    [InlineData("Admin,", "Admin")]
    [InlineData("", "")]
    [InlineData("   ", "")]
    [InlineData(null, "")]
    public void FirstNameFromDisplayOrLogonName(string? name, string expected) => Assert.Equal(expected, CurrentUser.FirstNameOf(name));

    /// <summary>The real lookup on this machine: never throws, never blank (it falls back to the logon name).</summary>
    [Fact]
    public void FirstNameOfThisAccount() => Assert.False(string.IsNullOrWhiteSpace(CurrentUser.FirstName()));
}
