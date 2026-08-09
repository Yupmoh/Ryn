using FluentAssertions;
using Ryn.Plugins.Dialog;
using Xunit;

namespace Ryn.Plugins.Tests;

public sealed class PickerMacScriptTests
{
    private static readonly PickerFilter[] CSharpFilters = [new PickerFilter { Name = "C#", Extensions = [".cs"] }];
    private static readonly PickerFilter[] CustomFilters = [new PickerFilter { Extensions = [".customext"] }];
    [Fact]
    public void Save_UsesDefaultNameForSuggestedFileName()
    {
        var script = PickerCommands.BuildMacScript("save", new PickerOptions
        {
            SuggestedFileName = "notes.cs"
        }, false);

        script.Should().Contain("choose file name");
        script.Should().Contain("default name \"notes.cs\"");
        script.Should().NotContain("default answer");
    }

    [Fact]
    public void OpenFile_UsesLiteralExtensionTypesWithoutBroadFallback()
    {
        var script = PickerCommands.BuildMacScript("file", new PickerOptions { Filters = CSharpFilters }, false);
        script.Should().Contain(@"of type {""cs""}");
        script.Should().NotContain("public.data");
    }

    [Fact]
    public void OpenFile_PreservesArbitraryExtensions()
    {
        var script = PickerCommands.BuildMacScript("file", new PickerOptions { Filters = CustomFilters }, false);
        script.Should().Contain(@"of type {""customext""}");
    }
}
