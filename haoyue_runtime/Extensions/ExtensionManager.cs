namespace Haoyue.Runtime.Extensions;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Haoyue.Runtime.Tools;

/// <summary>
/// Manages runtime extension discovery, initialization, and lifecycle.
/// Core runtime interacts with this manager instead of having hard static
/// dependencies on specific domain modules like Computer Use.
///
/// Extensions can be enabled or disabled individually at runtime: tool and prompt
/// registrations are tracked per extension id, and a disposed extension is
/// re-created from its recorded factory when re-enabled.
/// </summary>
public sealed class ExtensionManager : IAsyncDisposable
{
    private readonly List<IRuntimeExtension> _extensions = [];
    private readonly Dictionary<string, List<IDisposable>> _registrations = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Func<IRuntimeExtension>> _factories = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<IRuntimeExtension> Extensions => _extensions;

    public void Register(IRuntimeExtension extension)
    {
        ArgumentNullException.ThrowIfNull(extension);
        if (_extensions.All(e => e.Id != extension.Id))
        {
            var type = extension.GetType();
            _factories[extension.Id] = () => (IRuntimeExtension)Activator.CreateInstance(type)!;
            _extensions.Add(extension);
        }
    }

    public IRuntimeExtension? Get(string id) =>
        _extensions.FirstOrDefault(e => string.Equals(e.Id, id, StringComparison.OrdinalIgnoreCase));

    public bool TryGet(string id, out IRuntimeExtension? extension)
    {
        extension = Get(id);
        return extension is not null;
    }

    /// <summary>
    /// Creates an ExtensionManager with default discovered extensions via reflection.
    /// If an extension module is removed or excluded from compilation, this method
    /// automatically continues with remaining extensions without build or runtime breakage.
    /// </summary>
    public static ExtensionManager CreateDefault()
    {
        var manager = new ExtensionManager();
        try
        {
            var extensionInterface = typeof(IRuntimeExtension);
            var assembly = extensionInterface.Assembly;
            foreach (var type in assembly.GetTypes())
            {
                if (!type.IsAbstract &&
                    !type.IsInterface &&
                    extensionInterface.IsAssignableFrom(type) &&
                    type.GetConstructor(Type.EmptyTypes) is not null)
                {
                    try
                    {
                        if (Activator.CreateInstance(type) is IRuntimeExtension ext)
                        {
                            manager.Register(ext);
                        }
                    }
                    catch
                    {
                        // Extension discovery failure should never crash runtime initialization
                    }
                }
            }
        }
        catch
        {
            // Assembly scanning safety net
        }

        return manager;
    }

    public void InitializeAll(HaoyueRuntime runtime)
    {
        foreach (var ext in _extensions)
        {
            try
            {
                if (!ext.IsEnabled(runtime)) continue;
                // Idempotency guard: a repeated InitializeAll (e.g. advanced.set while
                // running) must not register the same tools twice.
                if (_registrations.ContainsKey(ext.Id)) continue;

                ext.Initialize(runtime);
                var tracked = new List<IDisposable>();
                ext.RegisterTools(new TrackingToolRegistry(runtime.Tools, tracked), runtime);
                _registrations[ext.Id] = tracked;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[ExtensionManager] Failed to initialize extension '{ext.Id}': {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Unloads one extension: disposes its tool registrations and the extension
    /// instance itself. Other extensions stay untouched. The extension id remains
    /// known, so <see cref="EnableAsync"/> can recreate it later.
    /// </summary>
    public async ValueTask DisableAsync(string id)
    {
        if (!_factories.ContainsKey(id)) return;

        if (_registrations.Remove(id, out var registrations))
        {
            foreach (var registration in registrations)
            {
                try { registration.Dispose(); } catch { }
            }
        }

        var ext = Get(id);
        if (ext is not null)
        {
            _extensions.Remove(ext);
            await ext.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// (Re-)enables one extension: recreates it from its factory, then initializes
    /// and registers its tools when its configuration says it is enabled. A no-op
    /// when the configuration leaves the extension off.
    /// </summary>
    public async ValueTask EnableAsync(HaoyueRuntime runtime, string id)
    {
        if (!_factories.TryGetValue(id, out var factory)) return;

        await DisableAsync(id).ConfigureAwait(false);

        var ext = factory();
        _factories[ext.Id] = factory;
        _extensions.Add(ext);

        if (!ext.IsEnabled(runtime)) return;

        try
        {
            ext.Initialize(runtime);
            var tracked = new List<IDisposable>();
            ext.RegisterTools(new TrackingToolRegistry(runtime.Tools, tracked), runtime);
            _registrations[ext.Id] = tracked;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[ExtensionManager] Failed to initialize extension '{ext.Id}': {ex.Message}");
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var registrations in _registrations.Values)
        {
            foreach (var registration in registrations)
            {
                try { registration.Dispose(); } catch { }
            }
        }
        _registrations.Clear();

        foreach (var ext in _extensions)
        {
            try
            {
                await ext.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[ExtensionManager] Failed to dispose extension '{ext.Id}': {ex.Message}");
            }
        }
        _extensions.Clear();
    }

    /// <summary>Records every tool registration an extension makes so it can be undone per extension.</summary>
    private sealed class TrackingToolRegistry(IToolRegistry inner, List<IDisposable> sink) : IToolRegistry
    {
        public IDisposable Register(ITool tool)
        {
            var registration = inner.Register(tool);
            sink.Add(registration);
            return registration;
        }

        public ITool? Resolve(string name) => inner.Resolve(name);

        public IReadOnlyList<ITool> All => inner.All;
    }
}
