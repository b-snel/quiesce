using System.Threading;

namespace Quiesce.Tests;

/// <summary>
/// The one place that stands up WPF for a test, and the one way to run code on an STA thread.
/// </summary>
/// <remarks>
/// <para>
/// Extracted because having two of them broke four unrelated tests. <see cref="ViewConstructionTests"/> owned
/// a private copy; a second test class added its own, and since xUnit runs distinct test classes in parallel
/// collections, whichever ran first won the race for
/// <see cref="System.Windows.Application.Current"/> — a per-AppDomain singleton. When the newer class won, it
/// installed a bare <c>Application</c> with no theme dictionary, on a short-lived STA thread that then exited,
/// leaving <c>Application.Current</c> owned by a dead dispatcher. The visible symptom was
/// <c>TryFindResource</c> returning null in a completely different file, which points at the brushes rather
/// than at the harness.
/// </para>
/// <para>
/// So: one initializer, one Application, one theme merge, and <see cref="Collection"/> to keep the classes
/// that use it off each other's threads. A shared collection serialises them; the one-shot guard is what makes
/// that correctness rather than luck.
/// </para>
/// </remarks>
internal static class WpfTestHost
{
    /// <summary>
    /// xUnit collection name. Every test class that touches WPF must carry
    /// <c>[Collection(WpfTestHost.Collection)]</c>, or it runs in parallel with the others and races the
    /// singleton again.
    /// </summary>
    public const string Collection = "wpf";

    private static readonly Lock AppLock = new();

    private static bool _initialized;

    /// <remarks>
    /// A bare <c>Application</c>, not <c>Quiesce.App.App</c>: the real one runs the single-instance guard in
    /// <c>OnStartup</c>, and merging <c>App.xaml</c> as a dictionary would CONSTRUCT an Application (its root
    /// element is <c>&lt;Application x:Class="…"&gt;</c>), which WPF permits only once per AppDomain.
    /// <c>Brushes.xaml</c> is a plain dictionary precisely so it can be merged here.
    /// <para>
    /// Constructing it also registers the <c>pack://</c> URI scheme, without which loading any asset by pack
    /// URI throws "Invalid URI: Invalid port specified" — a message that says nothing about the real cause.
    /// </para>
    /// </remarks>
    private static void EnsureApplication()
    {
        lock (AppLock)
        {
            if (_initialized)
            {
                return;
            }

            _initialized = true;

            var app = System.Windows.Application.Current ?? new System.Windows.Application();
            app.Resources.MergedDictionaries.Add(new System.Windows.ResourceDictionary
            {
                Source = new Uri("pack://application:,,,/Quiesce;component/Theme/Brushes.xaml", UriKind.Absolute),
            });
        }
    }

    /// <summary>Runs <paramref name="func"/> on a fresh STA thread with WPF initialised.</summary>
    public static T OnStaThread<T>(Func<T> func)
    {
        ArgumentNullException.ThrowIfNull(func);

        T? result = default;
        Exception? failure = null;
        var completed = false;

        var thread = new Thread(() =>
        {
            try
            {
                EnsureApplication();
                result = func();
                completed = true;
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        var joined = thread.Join(TimeSpan.FromSeconds(30));

        if (failure is not null)
        {
            throw new InvalidOperationException($"The STA thread threw: {failure}", failure);
        }

        // A Join TIMEOUT used to fall straight through to `return result!` with failure still null, so a hung
        // call surfaced as a NullReferenceException on whatever the caller did with the result - pointing at
        // the assertion instead of at the hang. Named explicitly.
        if (!joined || !completed)
        {
            throw new TimeoutException(
                "The STA thread did not finish within 30s. Something is hanging or blocking on the " +
                "dispatcher, which this harness never runs.");
        }

        return result!;
    }
}
