using System.Reflection;
using PuppeteerSharp;

namespace SjolystInnovation.AppKit.Pdf.Tests;

/// <summary>
/// Stands in for Chromium. Only the members the renderer uses do anything. The rest return
/// completed tasks and default values.
/// </summary>
internal sealed class FakeBrowser
{
    public static readonly byte[] Pdf = "%PDF-1.7 fake"u8.ToArray();

    public bool Connected { get; set; } = true;
    // Renders run in parallel in some tests, so the counts and lists are updated under a lock.
    private readonly Lock _lock = new();
    private int _disposed;
    private int _pagesOpened;
    private int _pagesDisposed;

    public int Disposed => _disposed;
    public int PagesOpened => _pagesOpened;
    public int PagesDisposed => _pagesDisposed;
    public List<string> Html { get; } = [];
    public List<PdfOptions> PdfOptions { get; } = [];
    public List<SetContentOptions?> ContentOptions { get; } = [];

    /// <summary>Runs when a page loads its content, so a test can make it hang or fail.</summary>
    public Func<Task>? OnSetContent { get; set; }

    public IBrowser Browser { get; }

    public FakeBrowser()
    {
        var browser = DispatchProxy.Create<IBrowser, Proxy>();
        ((Proxy)(object)browser).Handle = HandleBrowser;
        Browser = browser;
    }

    private object? HandleBrowser(MethodInfo method, object?[] args) => method.Name switch
    {
        "get_IsConnected" => Connected,
        "get_IsClosed" => Disposed > 0,
        "NewPageAsync" => Task.FromResult(NewPage()),
        "DisposeAsync" => Count(() => Interlocked.Increment(ref _disposed)),
        "CloseAsync" => CountTask(() => Interlocked.Increment(ref _disposed)),
        _ => Proxy.Default(method.ReturnType),
    };

    private IPage NewPage()
    {
        Interlocked.Increment(ref _pagesOpened);
        var page = DispatchProxy.Create<IPage, Proxy>();
        ((Proxy)(object)page).Handle = HandlePage;
        return page;
    }

    private object? HandlePage(MethodInfo method, object?[] args)
    {
        switch (method.Name)
        {
            case "SetContentAsync":
                lock (_lock)
                {
                    Html.Add((string)args[0]!);
                    ContentOptions.Add(args.Length > 1 ? args[1] as SetContentOptions : null);
                }
                return OnSetContent?.Invoke() ?? Task.CompletedTask;
            case "PdfDataAsync":
                lock (_lock) PdfOptions.Add((PdfOptions)args[0]!);
                return Task.FromResult(Pdf);
            case "DisposeAsync":
                return Count(() => Interlocked.Increment(ref _pagesDisposed));
            default:
                return Proxy.Default(method.ReturnType);
        }
    }

    private static object Count(Func<int> count)
    {
        count();
        return default(ValueTask);
    }

    private static Task CountTask(Func<int> count)
    {
        count();
        return Task.CompletedTask;
    }

    // DispatchProxy builds a subclass at run time, so the class cannot be sealed.
#pragma warning disable CA1852
    public class Proxy : DispatchProxy
#pragma warning restore CA1852
    {
        public Func<MethodInfo, object?[], object?> Handle { get; set; } = (m, _) => Default(m.ReturnType);

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            Handle(targetMethod!, args ?? []);

        public static object? Default(Type type)
        {
            if (type == typeof(void)) return null;
            if (type == typeof(Task)) return Task.CompletedTask;
            if (type == typeof(ValueTask)) return default(ValueTask);
            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Task<>))
            {
                var result = Default(type.GetGenericArguments()[0]);
                return typeof(Task).GetMethod(nameof(Task.FromResult))!.MakeGenericMethod(type.GetGenericArguments()[0]).Invoke(null, [result]);
            }
            return type.IsValueType ? Activator.CreateInstance(type) : null;
        }
    }
}
