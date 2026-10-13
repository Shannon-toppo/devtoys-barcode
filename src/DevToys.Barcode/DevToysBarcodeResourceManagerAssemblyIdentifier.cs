namespace DevToys.Barcode;

[Export(typeof(IResourceAssemblyIdentifier))]
[Name(nameof(DevToysBarcodeResourceManagerAssemblyIdentifier))]
internal sealed class DevToysBarcodeResourceManagerAssemblyIdentifier : IResourceAssemblyIdentifier
{
    public ValueTask<FontDefinition[]> GetFontDefinitionsAsync()
    {
        throw new NotImplementedException();
    }
}
