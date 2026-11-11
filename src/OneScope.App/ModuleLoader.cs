using System.Reflection;
using System.Runtime.Loader;
using OneGame.Control;
using OneScope.Sdk;

namespace OneScope.App;

/// <summary>
/// A module loaded from a path into its own <see cref="AssemblyLoadContext"/>. Disposing it unloads the context,
/// so two versions of one library's module can be loaded side by side and dropped again.
/// </summary>
internal sealed class LoadedModule : IDisposable
{
    private ModuleLoadContext? _context;

    internal LoadedModule(IScopeModule module, string path, ModuleLoadContext context)
    {
        Module = module;
        Path = path;
        _context = context;
    }

    /// <summary>The module instance, created from the first public parameterless <see cref="IScopeModule"/> in the assembly.</summary>
    public IScopeModule Module { get; }

    /// <summary>The assembly file the module came from.</summary>
    public string Path { get; }

    /// <summary>Whether the load context has been asked to unload.</summary>
    public bool IsUnloaded => _context is null;

    /// <summary>The load context's weak handle, for checking that an unloaded module was collected.</summary>
    public WeakReference ContextReference { get; private set; } = new(null);

    internal void Track() => ContextReference = new WeakReference(_context, trackResurrection: true);

    /// <inheritdoc />
    public void Dispose()
    {
        _context?.Unload();
        _context = null;
    }
}

/// <summary>
/// The only place in the app that uses reflection. A module assembly loads into a collectible context. The framework, onegame
/// and the SDK come from the host, so the module and the shell agree on every contract type.
/// </summary>
internal static class ModuleLoader
{
    /// <summary>Loads the module assembly at <paramref name="path"/>.</summary>
    /// <exception cref="ControlException"><c>not_found</c> when the file is missing; <c>bad_module</c> when it holds no usable module.</exception>
    public static LoadedModule Load(string path)
    {
        string full = System.IO.Path.GetFullPath(path);
        if (!File.Exists(full))
        {
            throw new ControlException("not_found", $"Module assembly '{path}' does not exist; expected a .dll built against RecursiveAI.OneScope.Sdk.");
        }

        var context = new ModuleLoadContext(full);
        try
        {
            Assembly assembly = context.LoadFromAssemblyPath(full);
            Type[] types;
            try
            {
                types = assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                throw new ControlException("bad_module", $"Module assembly '{path}' could not load its types: {ex.LoaderExceptions.FirstOrDefault()?.Message ?? ex.Message}");
            }

            foreach (Type type in types)
            {
                if (type.IsAbstract || type.IsInterface || !typeof(IScopeModule).IsAssignableFrom(type))
                {
                    continue;
                }

                // A constructor whose parameters all have defaults counts as parameterless: a module may take options with defaults.
                ConstructorInfo? ctor = type.GetConstructors().FirstOrDefault(c => c.GetParameters().All(p => p.IsOptional && p.HasDefaultValue));
                if (ctor is null)
                {
                    continue;
                }

                object?[] args = ctor.GetParameters().Select(p => p.DefaultValue).ToArray();
                var module = (IScopeModule)ctor.Invoke(args);
                var loaded = new LoadedModule(module, full, context);
                loaded.Track();
                return loaded;
            }

            throw new ControlException("bad_module", $"Module assembly '{path}' has no public class that implements IScopeModule and can be created without arguments (a constructor whose parameters all have defaults is fine).");
        }
        catch (BadImageFormatException ex)
        {
            context.Unload();
            throw new ControlException("bad_module", $"'{path}' is not a .NET assembly: {ex.Message}");
        }
        catch
        {
            context.Unload();
            throw;
        }
    }
}

internal sealed class ModuleLoadContext : AssemblyLoadContext
{
    private readonly AssemblyDependencyResolver _resolver;

    public ModuleLoadContext(string path)
        : base("module:" + System.IO.Path.GetFileNameWithoutExtension(path), isCollectible: true)
    {
        _resolver = new AssemblyDependencyResolver(path);
    }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        string? name = assemblyName.Name;
        if (name is null || IsShared(name))
        {
            return null;
        }

        string? found = _resolver.ResolveAssemblyToPath(assemblyName);
        return found is null ? null : LoadFromAssemblyPath(found);
    }

    private static bool IsShared(string name) =>
        name.StartsWith("System.", StringComparison.Ordinal)
        || name.StartsWith("Microsoft.", StringComparison.Ordinal)
        || name.StartsWith("OneGame.", StringComparison.Ordinal)
        || name.StartsWith("RecursiveAI.OneGame.", StringComparison.Ordinal)
        || name == "System"
        || name == "netstandard"
        || name == "mscorlib"
        || name == "RecursiveAI.OneScope.Sdk";
}
