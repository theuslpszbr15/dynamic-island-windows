using DynamicIsland;
using Xunit;

namespace DynamicIsland.Tests;

public class SeasonsTests
{
    [Theory]
    [InlineData(2026, 10, 31, "Halloween", PetOutfit.Bruxa)]
    [InlineData(2026, 10, 25, "Halloween", PetOutfit.Bruxa)]
    [InlineData(2026, 12, 24, "Natal", PetOutfit.Noel)]
    [InlineData(2026, 12, 31, "Ano-Novo", PetOutfit.Festa)]
    [InlineData(2027, 1, 1, "Ano-Novo", PetOutfit.Festa)]
    [InlineData(2026, 6, 24, "Festa junina", PetOutfit.Palha)]
    public void TheIslandAndTheMascotDressForTheSameDays(int year, int month, int day, string season, PetOutfit outfit)
    {
        var date = new DateTime(year, month, day);
        Assert.Equal(season, Seasons.For(date)?.Name);
        Assert.Equal(outfit, PetView.Resolve(PetOutfit.Auto, date));
    }

    [Fact]
    public void TheRestOfTheYearIsPlain()
    {
        Assert.Null(Seasons.For(new DateTime(2026, 11, 24)));
        Assert.Null(Seasons.For(new DateTime(2026, 10, 24)));
        Assert.Equal(4, Seasons.For(new DateTime(2026, 12, 1))!.Rim.Length);
    }
}
