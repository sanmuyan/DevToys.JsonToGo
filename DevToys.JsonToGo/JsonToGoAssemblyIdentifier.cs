using DevToys.Api;
using System.ComponentModel.Composition;

namespace DevToys.JsonToGo;

[Export(typeof(IResourceAssemblyIdentifier))]
[Name(nameof(JsonToGoAssemblyIdentifier))]
internal sealed class JsonToGoAssemblyIdentifier : IResourceAssemblyIdentifier
{
    public ValueTask<FontDefinition[]> GetFontDefinitionsAsync()
    {
        throw new NotImplementedException();
    }
}