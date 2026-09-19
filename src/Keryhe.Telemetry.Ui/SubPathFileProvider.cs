using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Primitives;

namespace Keryhe.Telemetry.Ui;

/// <summary>
/// Re-roots an existing <see cref="IFileProvider"/> at a subdirectory, so paths that reach it
/// look like they start there instead of at the wrapped provider's real root.
///
/// Needed because ASP.NET Core's static web asset pipeline serves a Razor class library's own
/// <c>wwwroot</c> at <c>/_content/{AssemblyName}/...</c> — correct for a component library
/// shipping a stylesheet or a script tag, wrong for a full single-page app whose
/// <c>&lt;base href="/"&gt;</c> and fingerprinted asset references all assume the origin root.
/// Wrapping <see cref="Microsoft.AspNetCore.Hosting.IWebHostEnvironment.WebRootFileProvider"/>
/// with this prefix — rather than pointing a fresh <c>PhysicalFileProvider</c> at the same
/// directory on disk — lets <c>Keryhe.Telemetry.Ui</c> serve its packaged bundle from "/" while
/// still resolving through whichever mechanism actually has the files: a <c>PhysicalFileProvider</c>
/// in a published app, but the generated static-web-assets manifest under <c>dotnet run</c> with a
/// project reference, where the files may not be physically under <c>wwwroot</c> at all.
/// </summary>
internal sealed class SubPathFileProvider(IFileProvider inner, string prefix) : IFileProvider
{
    private readonly string _prefix = prefix.Trim('/');

    public IFileInfo GetFileInfo(string subpath) => inner.GetFileInfo(Combine(subpath));

    public IDirectoryContents GetDirectoryContents(string subpath) => inner.GetDirectoryContents(Combine(subpath));

    public IChangeToken Watch(string filter) => inner.Watch(Combine(filter));

    private string Combine(string subpath)
    {
        subpath = subpath.TrimStart('/');
        return subpath.Length == 0 ? _prefix : $"{_prefix}/{subpath}";
    }
}
