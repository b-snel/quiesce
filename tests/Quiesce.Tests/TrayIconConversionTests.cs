using System.IO;
using System.Threading;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Quiesce.App;
using Xunit;

namespace Quiesce.Tests;

/// <summary>
/// That every icon the tray assigns is something Windows can actually display, and that the reason the code
/// takes the route it does is still true.
/// </summary>
/// <remarks>
/// <para>
/// This is the test whose absence cost a working application. The M7 plan reasoned that <c>TaskbarIcon</c>
/// could not be unit-tested — true, and still true: it needs a message pump the STA helper does not run, and
/// constructing one would leave a live icon in the test host's notification area. The mistake was concluding
/// that therefore nothing about the icon was testable. Building the icon is testable, it touches no window
/// and no shell, and that is where the bug was.
/// </para>
/// <para>
/// What happened: <c>BuildAttentionIcon</c> returned a <see cref="RenderTargetBitmap"/> for assignment to
/// <c>TaskbarIcon.IconSource</c>, whose setter resolves an <see cref="ImageSource"/> through a switch that
/// throws on anything it cannot get a URI out of. It killed the process, because it ran on the WPF dispatcher
/// with no handler above it, and only when <c>TrayPresentation.NeedsAttention</c> was true — so the app
/// launched fine on a clean machine and died on an engaged one. A WinExe has no console: no window, no
/// message, nothing. The sign-in scheduled task recorded it as result 0xE0434352, which reads like the task
/// failing rather than the app crashing.
/// </para>
/// </remarks>
[Collection(WpfTestHost.Collection)]
public class TrayIconConversionTests
{
    [Fact]
    public void Every_icon_the_tray_assigns_is_a_real_icon_with_pixels()
    {
        var failures = OnStaThread(() =>
        {
            var problems = new List<string>();

            foreach (var (name, icon) in TrayIcon.AllIcons())
            {
                try
                {
                    if (icon.Handle == nint.Zero)
                    {
                        problems.Add($"{name}: null handle");
                    }

                    // Round-tripping through a bitmap is the cheapest proof that the handle refers to a real
                    // image rather than merely being non-zero.
                    using var bitmap = icon.ToBitmap();

                    if (bitmap.Width == 0 || bitmap.Height == 0)
                    {
                        problems.Add($"{name}: {bitmap.Width}x{bitmap.Height}");
                    }
                }
                catch (Exception ex)
                {
                    problems.Add($"{name}: {ex.GetType().Name}: {ex.Message}");
                }
            }

            return problems;
        });

        Assert.True(
            failures.Count == 0,
            "An icon the tray assigns is not usable, which on the real dispatcher takes the whole process " +
            "down with no window and no message: " + string.Join("; ", failures));
    }

    [Fact]
    public void The_notify_icon_library_still_cannot_take_an_in_memory_image_source()
    {
        // The reason TrayIcon uses TaskbarIcon.Icon instead of TaskbarIcon.IconSource, pinned so that the
        // reason is checked rather than merely asserted in a comment. BOTH arms matter:
        //
        //   RenderTargetBitmap    -> NotImplementedException. This was the actual crash.
        //   PNG-decoded frame     -> UriFormatException. This was the OBVIOUS fix, and it does not work;
        //                            the arm that accepts frames goes looking for the URI it was loaded from.
        //
        // If a future H.NotifyIcon accepts either, this test fails, and that failure is the signal that
        // IconSource has become viable and the System.Drawing route in TrayIcon can be reconsidered. Until
        // then, deleting this test deletes the evidence.
        var (fromRenderTarget, fromDecodedFrame) = OnStaThread(() =>
        {
            var rendered = new RenderTargetBitmap(32, 32, 96, 96, PixelFormats.Pbgra32);
            rendered.Freeze();

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(rendered));
            var stream = new MemoryStream();
            encoder.Save(stream);
            stream.Position = 0;
            var decoded = BitmapFrame.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);

            return (Attempt(rendered), Attempt(decoded));
        });

        Assert.Equal("NotImplementedException", fromRenderTarget);
        Assert.Equal("UriFormatException", fromDecodedFrame);
    }

    [Fact]
    public void The_shipped_asset_is_reachable_by_its_pack_uri()
    {
        // Its own test because the failure is silent and total: Assets\quiesce.ico is <ApplicationIcon>, which
        // embeds it in the PE for Explorer to draw and does NOT make it reachable by a pack URI. It has to be
        // listed as <Resource> as well. Get it wrong and the app throws after the UAC prompt, with no window
        // and no error - the same shape of failure as the icon bug this file exists for.
        var size = OnStaThread(() =>
        {
            using var icon = TrayIcon.LoadPlainIcon();
            return (icon.Width, icon.Height);
        });

        Assert.True(size.Width > 0 && size.Height > 0, $"the plain icon loaded as {size.Width}x{size.Height}");
    }

    /// <summary>
    /// Names the exception the library throws for one image source, or <c>"converted"</c> if it succeeds.
    /// </summary>
    /// <remarks>
    /// The <see cref="ImageSource"/> parameter type is the whole point and must not be narrowed. <c>ToStream</c>
    /// is overloaded on <see cref="ImageSource"/>, <see cref="BitmapSource"/> and <see cref="BitmapFrame"/>, and
    /// a <see cref="RenderTargetBitmap"/> argument binds the SPECIFIC <c>BitmapSource</c> overload at compile
    /// time — which succeeds, and is not the method <c>IconSource</c> reaches. The first version of this test
    /// did exactly that and passed against the very bitmap that was crashing the app. The reported stack trace
    /// names <c>ToStreamAsync(ImageSource, …)</c>, so that is the one worth asserting on.
    /// </remarks>
    private static string Attempt(ImageSource source)
    {
        try
        {
            using var stream = H.NotifyIcon.ImageExtensions
                .ToStreamAsync(source, CancellationToken.None)
                .GetAwaiter()
                .GetResult();

            return "converted";
        }
        catch (Exception ex)
        {
            return ex.GetType().Name;
        }
    }

    /// <remarks>
    /// The shared host, not a local copy. A local copy is what broke four tests in
    /// <see cref="ViewConstructionTests"/> — see the remarks on <see cref="WpfTestHost"/>. WPF also needs an
    /// <c>Application</c> here for a reason specific to this file: constructing one registers the
    /// <c>pack://</c> scheme, and without it loading the icon asset throws "Invalid URI: Invalid port
    /// specified", which says nothing at all about the real cause.
    /// </remarks>
    private static T OnStaThread<T>(Func<T> func) => WpfTestHost.OnStaThread(func);
}
