namespace DevToys.Barcode.Tests;

/// <summary>
/// The tests of the command line tool replace <see cref="Console.Out"/> and <see cref="Console.Error"/>, which belong to the
/// whole process. xUnit runs the classes of a collection one after the other, and this collection does not run at the same
/// time as any other: nothing else can write to the console, or replace it, while one of these tests captures it.
/// (DevToys' own tests of its command line tools do the same with their TestParallelizationDisabled collection.)
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ConsoleTestCollection
{
    public const string Name = "Tests that capture the console";
}
