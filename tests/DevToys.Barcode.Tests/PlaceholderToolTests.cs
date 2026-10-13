using System.ComponentModel.Composition;
using System.Reflection;
using DevToys.Api;

namespace DevToys.Barcode.Tests;

public class PlaceholderToolTests
{
    private static Assembly LoadExtensionAssembly() => Assembly.Load("Shannon.DevToys.Barcode");

    [Fact]
    public void ResourceAssemblyIdentifier_IsExportedForMef()
    {
        Type? type = LoadExtensionAssembly().GetType("DevToys.Barcode.DevToysBarcodeResourceManagerAssemblyIdentifier");

        Assert.NotNull(type);
        Assert.True(typeof(IResourceAssemblyIdentifier).IsAssignableFrom(type));
        Assert.Contains(
            type!.GetCustomAttributes<ExportAttribute>(),
            export => export.ContractType == typeof(IResourceAssemblyIdentifier));
    }

    [Fact]
    public void PlaceholderTool_IsExportedAsGuiTool()
    {
        Type? type = LoadExtensionAssembly().GetType("DevToys.Barcode.Tools.EncodersDecoders.BarcodeEncoderDecoderGuiTool");

        Assert.NotNull(type);
        Assert.True(typeof(IGuiTool).IsAssignableFrom(type));
        Assert.Contains(
            type!.GetCustomAttributes<ExportAttribute>(),
            export => export.ContractType == typeof(IGuiTool));
    }
}
